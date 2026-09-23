using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using HarmonyLib;
using SimpleJSON;
using UnityEngine;

namespace Quest3TriggerUI
{
    // Speeds up BrowserAssist's "rescan local + VAR" button. BA re-opens
    // every .var archive and re-parses every local resource file on each
    // rescan, which takes minutes on large libraries even when nothing
    // changed. This accelerator fingerprints the file tree (path + size +
    // mtime) and Harmony-patches four BA methods:
    //
    //   RescanPackages — the actual button path: BrowserFiltersUI.
    //       RefreshContents' coroutine calls this (SuperController-level
    //       rescan + meta dependency processing = the dominant cost).
    //       Skipped when the library is unchanged; ruleset-bearing calls
    //       (forceApplyRuleset / VAR-management toggles) always run native.
    //   RescanAllVARResources — the settings-tab rescan entry; skipped
    //       entirely when the VAR library fingerprint is unchanged.
    //   RescanVARResources — called per package inside the full rescan;
    //       skipped for packages whose fingerprint is unchanged, so the
    //       expensive in-zip file enumeration only runs for new/changed vars.
    //   LocalResourcesInit — skipped entirely when the local resource tree
    //       fingerprint is unchanged.
    //
    // Everything is resolved by reflection against JayJayWon's assembly
    // (no compile-time dependency) and every prefix falls back to the
    // original method on any doubt.
    internal static class BrowserAssistScanAccelerator
    {
        internal static bool Enabled = true;

        private const BindingFlags StaticAll =
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags InstAll =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly string CachePath = Path.Combine(
            BepInEx.Paths.ConfigPath, "Quest3TriggerUI.ba-scan.json");

        private static Harmony _harmony;
        private static bool _installed, _failed;
        private static float _nextTry;

        // Resolved BA members.
        private static MethodInfo _postRescan;
        private static PropertyInfo _availableVars;
        private static FieldInfo _localRvges;
        private static MethodInfo _getVpme;          // VARPackageVersionGroupEntry.GetVPME(int)
        private static PropertyInfo _varPathProp;    // VARPackageManifestEntry.varFilePathName
        private static MethodInfo _localRootsMethod; // Const.GetLocalResourceRootFolderNames()
        private static PropertyInfo _varMgmtProp;    // Globals.isVARManagementActive
        private static FieldInfo _varMgmtRuleSet;    // VARMgmtSettings.varMgmtCurrentRuleSetJSE
        private static FieldInfo _varMgmtRuleSetList;// VARMgmtSettings.varManagementRuleSets
        private static string _offloadRoot;
        private static string _metaCacheRoot;        // Const.VARMetaCacheFolder
        private static string _baSettingsFile;       // BAScriptDataFolder\BASettings.cfg
        private static int _inVarAll;                // RescanAllVARResources nesting depth
        private static bool _varSkippedPass;         // VAR side skipped in this RefreshContents pass
        private static bool _refreshSkipped;         // both halves skipped — trailing refresh is pure overhead
        private static System.Diagnostics.Stopwatch _postSw;
        private static System.Diagnostics.Stopwatch _stageSw;
        private static MethodInfo _pkgPathToUid;    // FileManager.packagePathToUid
        private static MethodInfo _fmRegister;      // FileManager.RegisterPackage
        private static MethodInfo _fmUnregister;    // FileManager.UnregisterPackage
        private static MethodInfo _fmByUid;         // FileManager.GetPackagesByUid
        private static PropertyInfo _fmRefreshTime; // FileManager.lastPackageRefreshTime
        private static FieldInfo _refreshHandlers;  // FileManager.onRefreshHandlers
        private static string _packageFolder;       // FileManager.packageFolder
        private const int MaxIncrementalPkgs = 64;
        // Incremental local-resource registration
        private static MethodInfo _getResType;      // ResourceManifest.GetResourceType
        private static MethodInfo _getPresetType;   // ResourceManifest.GetPresetAtomType
        private static MethodInfo _regLocal;        // ResourceManifest.RegisterLocalResourceOfType
        private static MethodInfo _purgeLocal;      // ResourceManifest.PurgeLocalRVGE
        private static FieldInfo _rvgeDictField;    // ResourceManifest.resourceVersionGroupsDict
        private static MethodInfo _getFolder;       // FolderBrowser.GetFolder
        private static MethodInfo _getResCat;       // ResourceType.GetResourceCategory
        private static MethodInfo _regExistingUD;   // DataFileManifest.RegisterExisitingDFUserLocalResource
        private static MethodInfo _regUserSaveReq;  // DataFileManifest.RegisterLocalResourceUserSaveRequest
        private static int _rcPreset = 1;           // ResourceCategory.preset
        private static int _rtPresetAtom = -1, _rtUnknown = 0;
        private static int _rtClothingItemPresets = -1, _rtHairItemPresets = -1;
        private static int _rtFemaleClothing = -1, _rtMaleClothing = -1;
        private static int _rtFemaleHair = -1, _rtMaleHair = -1;
        private const int MaxIncrementalLocal = 128;
        private static Dictionary<string, long> _localFileCache, _localFilePending;
        private static bool _localClothingHairDirty;
        private static bool _inPostRescan;
        private static bool _chRebuildRan;   // first callbacks pass ran this scan
        // Quick rescan ("轻扫") — per-package BA manifest update without
        // touching scene atoms.
        private static bool _inQuickScan;
        private static Type _vpmeType, _vpvgeType;
        private static MethodInfo _vpmeFromPath;    // GetVPMEFromPath(path,disabled)
        private static MethodInfo _vpmeByKey;       // GetVPME(vpmeKey)
        private static MethodInfo _vpmeKeyFromFile; // GetVPMEKey(filename)
        private static MethodInfo _vpmeKeyOf;       // GetVPMEKey(vpme)
        private static MethodInfo _vpvgeKeyOf;      // GetVPVGEKey(vpme)
        private static MethodInfo _refreshManifestNew; // RefreshManifestWithNewVARs
        private static MethodInfo _refreshVarPrefs;    // RefreshVARFilePrefs
        private static MethodInfo _procMetaDepsAll;    // ProcessVARMetaFilesAndDependencies
        private static MethodInfo _refreshCombiTags;   // RefreshAllVARDependencyCombiTags
        private static MethodInfo _cachePresetRvge;    // CachePrimaryClothingItemPresetRVGEs
        private static PropertyInfo _varGroupsProp;    // varPackageVersionGroups
        private static MethodInfo _vpmeSetNA;          // vpme.SetNotBAAvailable
        private static FieldInfo _vpmeVersion;         // vpme.varVersion
        private static PropertyInfo _vpmeVersionProp;
        private static MethodInfo _vpvgeSetMissing;    // vpvge.SetVARMissing
        private static MethodInfo _clothingHairCB;     // AtomUtils.RefreshClothingHairItemCallbacks(bool)
        private static PropertyInfo _vpmeVpvge;        // vpme.vpvge
        private static MethodInfo _vpvgeResPaths;      // vpvge.GetResourcePaths(int)
        private static bool _quickScanSawCH;           // last quick scan touched clothing/hair
        // Content class of the current fingerprinted diff. Only content that
        // lands in a Person atom clothing/hair/morph list gives the
        // scene-side rebuilds (per-atom RefreshDynamicItems /
        // RefreshPackageMorphs + the clothing-hair callback pass) anything to
        // do; everything else is skipped. The verdict only applies while the
        // pass that produced it is still running, so a plain user action
        // never rides a stale decision.
        private static bool _sceneRebuildDecided;
        private static bool _sceneRebuildNeeded;
        private static string _sceneRebuildWhy;
        // Per-class verdict of the current fingerprinted diff. Clothing
        // items, hair items and package morphs are rebuilt by three separate
        // primitives, so each one only runs when its own class moved.
        private static bool _needClothing, _needHair, _needMorphs;
        private static int _pkgClothing, _pkgHair, _pkgMorphs;
        // Local side of the same split, set by TryIncrementalLocalUpdate, so
        // a locally moved file only invalidates the class it belongs to.
        private static bool _localClassClothing, _localClassHair, _localClassMorphs;
        private static bool _localClassUnknown;
        // The verdict only covers the selectors that were alive when it was
        // made: a person created afterwards (add person, scene reload) has to
        // sync natively, and that first native call is what fills its
        // catalog. An empty snapshot therefore disables the shortcut.
        private static HashSet<int> _passSelectors;
        // Per-primitive cost accounting for the current pass.
        private static System.Diagnostics.Stopwatch _swClothes, _swHair, _swMorph;
        private static double _msClothes, _msHair, _msMorph;
        private static int _nClothes, _nHair, _nMorph;
        private static int _skipClothes, _skipHair, _skipMorph;
        private static int _incMorphPkgs;
        private static int _morphPurgePkgs, _morphPurgeItems, _morphPurgeZeroed;
        // Class-gate members, resolved once at install. A missing member only
        // disables the matching shortcut, never the native path.
        private static MethodInfo _dynClothes, _dynHair;
        private static FieldInfo _dcsFemaleClothingC, _dcsMaleClothingC;
        private static FieldInfo _dcsFemaleHairC, _dcsMaleHairC;
        private static MethodInfo _morphBankRefresh;
        private static FieldInfo _morphBankAutoFolder, _morphBankLoadedUids;
        private static FieldInfo _morphBankPreloadUids;
        private static Type _morphBankType;
        private static MethodInfo _morphImportDir, _morphRebuildLookups;
        private static MethodInfo _morphSubBankComplete, _getSubBanks;
        private static MethodInfo _fmFindVarDirs;
        private static PropertyInfo _varDirPackageProp, _pkgUidProp, _pkgGroupProp;
        private static MethodInfo _pkgGroupCustomOption;
        private static Type _morphSubBankType;
        // Incremental morph removal members. Every package-imported DAZMorph
        // records its source package uid and lives in the sub bank
        // _packageMorphs list, so the morphs of a dropped package can be
        // unlinked in place instead of paying the native wipe plus the whole
        // tree re-import.
        private static FieldInfo _morphPkgUidField, _morphStartValueField;
        private static PropertyInfo _morphValueProp, _subPackageMorphsProp;
        private static MethodInfo _subBankRebuildDicts;
        private static Type _utilsType;
        private static MethodInfo _utilsListFiles;   // Utils.GetFilesAtPathRecursive
        internal static bool SkipPackageListSync;    // config [BA]SkipPackageListSync

        // Persisted fingerprints. VAR side keeps a per-package map
        // (varFilePathName -> "size|mtime") so a partial rescan knows which
        // packages changed; the package count is naturally small. The local
        // side CANNOT afford a per-file dictionary — Saves+Custom hold
        // hundreds of thousands of files and a managed map that large trips
        // Boehm GC's "too many heap sections" fatal error — so it stores a
        // per-root order-independent rolling hash + count instead.
        private static Dictionary<string, string> _varCache;
        // Per-package content-kind memory, keyed like _varCache. A package
        // that has been listed once keeps the answer to "which classes did
        // it feed", so a later DELETION of that package no longer has to be
        // read as "class unknown" — that verdict is what forced the ~13.5s
        // clothing+hair rebuild on every removal pass. Entries are kept on
        // purpose after the package is gone: that memory is exactly what
        // the next removal pass reads. Chars: c = clothing, b = male
        // clothing, h = hair, m = morphs; an empty string means "listed
        // successfully, none of these". Packages that predate the map get
        // their verdict from SeedKindMemory at install time.
        private static Dictionary<string, string> _varKindCache;
        private static long[] _localHashCache, _localCountCache;
        private static bool _localCacheSet;
        private static bool _cacheLoaded;

        // ScanForVARs attribution + VaM mount-probe memo. BA re-enumerates
        // the whole var tree and re-probes every mapped path through
        // FileManagerSecure on every rescan that has any change; the
        // counters say which half owns that time and the probe memo answers
        // it from memory once a path has been asked about. Entries drop out
        // as soon as the fingerprint marks their path dirty, so a rescan
        // that added or replaced one package still pays only for that one.
        private static bool _inScanForVars;
        private static System.Diagnostics.Stopwatch _sfvSw, _sfvEnumSw, _sfvProbeSw, _sfvVpmeSw;
        private static long _sfvEnumTicks, _sfvDirTicks, _sfvVpmeTicks;
        private static int _sfvEnumN, _sfvDirN, _sfvDirHit, _sfvVpmeN;
        private static Dictionary<string, bool> _sfvDirMemo;

        // Pass-scoped reuse of VaM's directory listings. Every
        // FileManagerSecure.GetFiles/GetDirectories call builds a fresh
        // SystemDirectoryEntry — a real disk enumeration, not a cache
        // lookup — and BA's walk asks once per directory per pass (about
        // 4.8k calls, 6.8s on the 2.4k-directory AddonPackages tree). A
        // listing only changes when that directory's own entry set
        // changes, which NTFS records in the directory's mtime, so a
        // listing is reused while its mtime holds and re-read the moment
        // it moves. Only real on-disk directories are memoized (mounted
        // var pseudo paths stay native) and only while BA's scan window is
        // open — every other caller keeps the native answer.
        private sealed class FmsListing
        {
            public string[] Values;
            public long Mtime;
        }
        private static readonly Dictionary<string, FmsListing> _fmsListings =
            new Dictionary<string, FmsListing>(StringComparer.OrdinalIgnoreCase);
        private const int FmsMaxListings = 20000;

        // The listing memo is only worth having on a cold session if it
        // survives the restart: mtimes outlive the process, so the file is
        // reloaded and each entry is re-validated by the same mtime check.
        // Line per key: kind \t dir \t pattern \t mtime \t name<SOH>name...
        private static readonly string FmsCachePath = Path.Combine(
            BepInEx.Paths.ConfigPath, "Quest3TriggerUI.ba-dirs.txt");
        private static bool _fmsLoaded, _fmsDirty;        private static long _fmsServeN, _fmsNativeN, _fmsStatTicks;
        [ThreadStatic] private static string _fmsPendKey;
        [ThreadStatic] private static long _fmsPendMtime;
        [ThreadStatic] private static bool _fmsPend;
        // In-flight scan state.
        private static Dictionary<string, string> _varPending;
        private static long[] _localHashPending, _localCountPending;
        private static bool _localPendingSet;
        private static HashSet<string> _varDirty;

        // The clothing/hair container sync re-walks the whole item tree through
        // FileManager.FindAllFiles once per Person atom, and that listing cannot
        // move between those atoms: the pass registers its packages before this
        // phase starts. The walk is memoized for the remainder of the pass and
        // dropped at every point that can move the library. The counters report
        // the walk/sort/reconcile split so the next step has numbers, not guesses.
        private static readonly Dictionary<string, object[]> _fafMemo =
            new Dictionary<string, object[]>(StringComparer.OrdinalIgnoreCase);
        private static bool _scsActive, _fafServed;
        private static long _scsStart, _fafCallStart, _sortCallStart;
        private static string _scsWhat;
        private static int _scsIn, _fafCallN, _fafCallSum, _fafServeCall;
        private static long _fafCallTicks;
        private static int _sortCallN, _sortCallSum;
        private static long _sortCallTicks;
        // The male catalog cannot have moved when the fingerprinted diff
        // carries no Custom/Clothing/Male entry, so its container sync is
        // skipped for that pass. VaM keeps its own per-searchPath file/meta
        // cache and turns it on only while a scene loads; the rebuild pass
        // brackets itself with that same toggle so the repeat syncs of the
        // remaining atoms read the cached tree instead of the disk.
        private static bool _needClothingMale, _maleSkipLogged, _syncItemsCacheOn;
        // A deleted package is absent from the new fingerprint, so the diff
        // used to come out empty on a pure removal and fall through to VaM
        // full package-index rebuild (~34s on this library). Removals now
        // ride the same incremental path; the morph class is the only one
        // whose rebuild is a full re-import per bank, so the banks are asked
        // whether a removed package ever contributed a morph.
        private static int _removedCount;
        internal static bool IncrementalRemoval = true;
        // Where the rest of RefreshDynamicClothes goes once the container sync
        // itself is cached: the per-atom walks feed InitClothingItems, which
        // then re-registers a clothing:* JSON bool / toggle for every item it
        // finds, and each selector UI resyncs. Both natives are only timed —
        // the next reading says which one is worth attacking.
        private static double _msInitClothes, _msResync;
        private static int _nInitClothes, _nResync;
        private static System.Diagnostics.Stopwatch _swInitClothes, _swResync;
        private static MethodInfo _dcsInitClothes, _dynUiResync;
        // A person's clothing selector is one class instantiated twice -
        // female catalogue and male catalogue - and the class carries no
        // content-type member, so the only dependable handle is the
        // selector's own field.
        private static FieldInfo _dynSelCharSelector, _dcsClothingMaleUi;
        // The engine resyncs every clothing/hair selector UI of every person
        // in the scene on each rescan — closed panels included. Rebuilding
        // those lists is the single biggest cost left in a pass, so a hidden
        // panel is skipped and marked; the moment it becomes visible the
        // resync is replayed once, so the panel still opens up to date.
        private static object _resyncInst;
        private static int _skipResyncUi, _skipResyncMaleUi;
        private static readonly List<UnityEngine.Component> _pendingResync =
            new List<UnityEngine.Component>();
        internal static bool DeferHiddenResync = true;
        private static int _skipClothesMale;
        private static MethodInfo _syncItemsCacheEnable, _syncItemsCacheDisable;

        // The scene-load accelerator validates its per-scene dependency records
        // against this stamp: any change to the VAR library invalidates every
        // record at once. Cached by dictionary identity — a scan replaces the
        // dictionary instead of mutating it, so the reference is a valid key.
        private static Dictionary<string, string> _stampSource;
        private static string _stampValue;

        internal static string LibraryStamp
        {
            get
            {
                if (_varCache == null) return null;
                if (ReferenceEquals(_stampSource, _varCache)) return _stampValue;
                _stampSource = _varCache;
                _stampValue = ComputeStamp(_varCache);
                return _stampValue;
            }
        }

        private static string ComputeStamp(Dictionary<string, string> cache)
        {
            try
            {
                long mix = 17;
                long count = 0;
                foreach (var kv in cache)
                {
                    count++;
                    mix = mix * 31 + kv.Key.Length;
                    string value = kv.Value == null ? "" : kv.Value;
                    for (int i = 0; i < value.Length; i++)
                        mix = mix * 33 + value[i];
                }
                return count + ":" + mix.ToString("x");
            }
            catch { return null; }
        }

        internal static void Tick()
        {
            PumpPendingResync();
            if (_installed || _failed || !Enabled ||
                Time.unscaledTime < _nextTry) return;
            _nextTry = Time.unscaledTime + 5f;
            TryInstall();
        }

        private static readonly System.Diagnostics.Stopwatch _baClock =
            System.Diagnostics.Stopwatch.StartNew();

        internal static void Log(string message)
        {
            Quest3TriggerUIPlugin.Log.LogInfo("[BA加速][" +
                DateTime.Now.ToString("HH:mm:ss.fff") + " +" +
                _baClock.ElapsedMilliseconds + "ms] " + message);
        }

        private static void TryInstall()
        {
            try
            {
                Assembly asm = AssemblyCatalog.FindByType("JayJayWon.VARPackageManifest");
                if (asm == null) return;   // BrowserAssist not loaded (yet)

                Type varManifest = asm.GetType("JayJayWon.VARPackageManifest");
                Type resManifest = asm.GetType("JayJayWon.ResourceManifest");
                Type vpvgeType = asm.GetType("JayJayWon.VARPackageVersionGroupEntry");
                Type vpmeType = asm.GetType("JayJayWon.VARPackageManifestEntry");
                Type constType = asm.GetType("JayJayWon.Const");
                if (varManifest == null || resManifest == null ||
                    vpvgeType == null || vpmeType == null || constType == null)
                { Fail("BA 类型解析失败"); return; }

                MethodInfo rescanAll = varManifest.GetMethod("RescanAllVARResources", StaticAll);
                MethodInfo rescanVarRes = varManifest.GetMethod("RescanVARResources", StaticAll);
                MethodInfo rescanPkgs = varManifest.GetMethod("RescanPackages",
                    StaticAll, null, new[] { typeof(bool) }, null);
                _postRescan = varManifest.GetMethod("PostRescanRefresh", StaticAll);
                MethodInfo localInit = resManifest.GetMethod("LocalResourcesInit", StaticAll);
                _availableVars = varManifest.GetProperty("availableVARPackages", StaticAll);
                _localRvges = resManifest.GetField("localRVGEs", StaticAll);
                _getVpme = vpvgeType.GetMethod("GetVPME",
                    BindingFlags.Instance | BindingFlags.Public, null,
                    new[] { typeof(int) }, null);
                _varPathProp = vpmeType.GetProperty("varFilePathName",
                    BindingFlags.Instance | BindingFlags.Public);
                _localRootsMethod = constType.GetMethod(
                    "GetLocalResourceRootFolderNames", StaticAll);
                FieldInfo offload = constType.GetField("VAROffloadFolder", StaticAll);
                _offloadRoot = offload == null ? null : offload.GetValue(null) as string;
                FieldInfo metaCache = constType.GetField("VARMetaCacheFolder", StaticAll);
                _metaCacheRoot = metaCache == null ? null : metaCache.GetValue(null) as string;
                FieldInfo baData = constType.GetField("BAScriptDataFolder", StaticAll);
                string baDataDir = baData == null ? null : baData.GetValue(null) as string;
                _baSettingsFile = baDataDir == null ? null :
                    Path.Combine(baDataDir, "BASettings.cfg");
                Type globalsType = asm.GetType("JayJayWon.Globals");
                _varMgmtProp = globalsType == null ? null :
                    globalsType.GetProperty("isVARManagementActive", StaticAll);
                Type mgmtType = asm.GetType("JayJayWon.VARMgmtSettings");
                if (mgmtType != null)
                {
                    _varMgmtRuleSet = mgmtType.GetField(
                        "varMgmtCurrentRuleSetJSE", StaticAll);
                    _varMgmtRuleSetList = mgmtType.GetField(
                        "varManagementRuleSets", StaticAll);
                }
                Type filtersType = asm.GetType("JayJayWon.BrowserFiltersUI");
                MethodInfo personClothing = filtersType == null ? null :
                    filtersType.GetMethod("RefreshPersonClothing", StaticAll);
                MethodInfo resetCounts = resManifest.GetMethod(
                    "ResetAllClothingHairItemPresetCounts", StaticAll);
                // RescanPackages ends with ProcessVARMetaFilesAndDependencies,
                // which re-parses every package's meta file even when only one
                // var changed — the dominant cost of a small update. The
                // per-package entry point lets us skip unchanged VPMEs (their
                // dep data persists on the reused vpme object).
                Type pcType = asm.GetType("JayJayWon.PC");
                MethodInfo metaDeps = pcType == null ? null :
                    pcType.GetMethod("ProcessMetaFileDependencies", StaticAll,
                        null, new[] { vpmeType }, null);

                if (rescanAll == null || rescanVarRes == null ||
                    rescanPkgs == null || _postRescan == null || localInit == null ||
                    _availableVars == null || _localRvges == null ||
                    _getVpme == null || _varPathProp == null)
                { Fail("BA 成员解析失败"); return; }

                _harmony = new Harmony("Quest3TriggerUI.baaccel");
                // Hot reload leaves the previous payload assembly resident
                // with its prefixes still registered under this same owner
                // id — both would fingerprint on every scan. Drop them first.
                _harmony.UnpatchAll(_harmony.Id);
                Type self = typeof(BrowserAssistScanAccelerator);
                _harmony.Patch(rescanAll,
                    prefix: Patch(self, "VarAllPrefix"),
                    finalizer: Patch(self, "VarAllFinalizer"));
                _harmony.Patch(rescanPkgs,
                    prefix: Patch(self, "VarPkgsPrefix"),
                    finalizer: Patch(self, "VarPkgsFinalizer"));
                _harmony.Patch(rescanVarRes,
                    prefix: Patch(self, "VarResPrefix"));
                _harmony.Patch(localInit,
                    prefix: Patch(self, "LocalPrefix"),
                    finalizer: Patch(self, "LocalFinalizer"));
                // On an all-unchanged pass the coroutine still rebuilds the
                // person clothing list and every preset count — pure overhead
                // when nothing moved. Both are gated to the RefreshContents
                // caller so their other call sites stay native.
                if (personClothing != null)
                    _harmony.Patch(personClothing,
                        prefix: Patch(self, "PersonClothingPrefix"));
                if (resetCounts != null)
                    _harmony.Patch(resetCounts,
                        prefix: Patch(self, "ResetCountsPrefix"));
                if (metaDeps != null)
                    _harmony.Patch(metaDeps,
                        prefix: Patch(self, "MetaDepsPrefix"));
                // Time the refresh tail we deliberately keep running.
                _harmony.Patch(_postRescan,
                    prefix: Patch(self, "PostRescanPrefix"),
                    finalizer: Patch(self, "PostRescanFinalizer"));

                // Stage probes inside native RescanPackages — the process
                // hard-crashes (0x80000003, Boehm GC fatal class) somewhere
                // in there; the last "stage →" line before a crash names the
                // culprit stage. Harmless on the rare non-crash path.
                PatchStage(varManifest, "ScanForVARs");
                try
                {
                    MethodInfo sfvM = varManifest.GetMethod("ScanForVARs", StaticAll);
                    if (sfvM != null)
                        _harmony.Patch(sfvM,
                            prefix: Patch(self, "ScanForVarsEnter"),
                            finalizer: Patch(self, "ScanForVarsExit"));
                    MethodInfo vpmeM = FindStatic(varManifest, "GetVPMEFromPath",
                        typeof(string), typeof(bool));
                    if (vpmeM != null)
                        _harmony.Patch(vpmeM,
                            prefix: Patch(self, "VpmeProbeEnter"),
                            finalizer: Patch(self, "VpmeProbeExit"));
                    else Log("VPME 探针未找到 GetVPMEFromPath(string,bool)");
                    // ScanForVARs enumerates through the 5-arg overload and
                    // the private worker; the field-based _utilsType is not
                    // assigned yet at this point, so resolve it locally.
                    Type utilsProbe = asm.GetType("JayJayWon.Utils");
                    if (utilsProbe == null) Log("枚举探针未找到 JayJayWon.Utils");
                    else
                    {
                        // Name-agnostic: the shipped BA build may name the
                        // worker differently than the decompiled source. The
                        // outer call's time already contains the inner one's.
                        int mounted = 0;
                        foreach (MethodInfo m in utilsProbe.GetMethods(StaticAll))
                        {
                            if (m.Name != "GetFilesAtPathRecursive" &&
                                m.Name != "GetFilesAtPathRecursiveInternal")
                                continue;
                            if (m.GetParameters().Length < 5) continue;
                            _harmony.Patch(m,
                                prefix: Patch(self, "EnumProbeEnter"),
                                finalizer: Patch(self, "EnumProbeExit"));
                            mounted++;
                        }
                        Log("枚举探针挂载 " + mounted + " 个");
                    }
                    Type fmsT = typeof(SuperController).Assembly.GetType(
                        "MVR.FileManagementSecure.FileManagerSecure");
                    if (fmsT != null)
                    {
                        MethodInfo deM = FindStatic(fmsT, "DirectoryExists",
                            typeof(string));
                        if (deM != null)
                            _harmony.Patch(deM,
                                prefix: Patch(self, "DirProbePrefix"),
                                finalizer: Patch(self, "DirProbeExit"));
                        else
                        {
                            deM = FindStatic(fmsT, "DirectoryExists",
                                typeof(string), typeof(bool));
                            if (deM != null)
                                _harmony.Patch(deM,
                                    prefix: Patch(self, "DirProbePrefix2"),
                                    finalizer: Patch(self, "DirProbeExit2"));
                        }
                        // Reuse the directory listings BA's walk asks for
                        // over and over — each native call rebuilds a fresh
                        // SystemDirectoryEntry (a real disk enumeration).
                        MethodInfo fmsFiles = FindStatic(fmsT, "GetFiles",
                            typeof(string), typeof(string));
                        if (fmsFiles != null)
                            _harmony.Patch(fmsFiles,
                                prefix: Patch(self, "FmsFilesPrefix"),
                                postfix: Patch(self, "FmsFilesPostfix"));
                        MethodInfo fmsDirs = FindStatic(fmsT, "GetDirectories",
                            typeof(string), typeof(string));
                        if (fmsDirs != null)
                            _harmony.Patch(fmsDirs,
                                prefix: Patch(self, "FmsDirsPrefix"),
                                postfix: Patch(self, "FmsDirsPostfix"));
                        Log("目录枚举记忆挂载 " + (fmsFiles != null ? 1 : 0) +
                            "/" + (fmsDirs != null ? 1 : 0));                    }
                }
                catch (Exception e) { Log("ScanForVARs 内部探针挂载失败：" + e.Message); }                PatchStage(varManifest, "RefreshMissingVARs");
                PatchStage(varManifest, "RefreshManifestWithNewVARs");
                PatchStage(varManifest, "RefreshVARFilePrefs");
                PatchStage(varManifest, "ProcessVARMetaFilesAndDependencies");
                PatchStage(varManifest, "RefreshAllVARDependencyCombiTags");
                PatchStage(resManifest, "CachePrimaryClothingItemPresetRVGEs");
                // The clothing/hair container sync re-walks the whole item
                // tree once per Person atom through FileManager.FindAllFiles.
                // Probe the walk/sort/reconcile split and reuse the listing
                // across those atoms; the memo dies at every library change.
                try
                {
                    Type fmSync = typeof(SuperController).Assembly
                        .GetType("MVR.FileManagement.FileManager");
                    MethodInfo fafM = fmSync != null
                        ? FindStatic(fmSync, "FindAllFiles", 4) : null;
                    if (fafM != null)
                        _harmony.Patch(fafM, prefix: Patch(self, "FafPrefix"),
                            finalizer: Patch(self, "FafFinalizer"));
                    MethodInfo srtM = fmSync != null
                        ? FindStatic(fmSync, "SortFileEntriesByLastWriteTime", 1)
                        : null;
                    if (srtM != null)
                        _harmony.Patch(srtM, prefix: Patch(self, "SortPrefix"),
                            finalizer: Patch(self, "SortFinalizer"));
                    Type dcsSync = typeof(SuperController).Assembly
                        .GetType("DAZCharacterSelector");
                    MethodInfo scsM = null;
                    if (dcsSync != null)
                        foreach (MethodInfo m in dcsSync.GetMethods(InstAll))
                            if (m.Name == "SyncCustomItems" &&
                                m.GetParameters().Length == 5) { scsM = m; break; }
                    if (scsM != null)
                        _harmony.Patch(scsM, prefix: Patch(self, "ScsPrefix"),
                            finalizer: Patch(self, "ScsFinalizer"));
                    Log("服装容器同步挂载 列表" + (fafM != null ? "OK" : "×") +
                        " 排序" + (srtM != null ? "OK" : "×") + " 同步" +
                        (scsM != null ? "OK" : "×"));
                }
                catch (Exception e) { Log("服装容器同步挂载失败：" + e.Message); }
                // BA's RescanPackages opens with MemoryOptimizer.TriggerOptimize();
                // the deferred GC/UnloadUnusedAssets it schedules lands right
                // after the pass and blocks the main thread for seconds on a
                // 28GB working set — pure overhead on the incremental path,
                // which allocates almost nothing.
                try
                {
                    Type memOpt = typeof(SuperController).Assembly
                        .GetType("MemoryOptimizer");
                    if (memOpt == null)
                        Log("内存优化探针未找到 MemoryOptimizer 类型");
                    else
                    {
                        MethodInfo trig = memOpt.GetMethod("TriggerOptimize",
                            BindingFlags.Instance | BindingFlags.Public |
                            BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                        if (trig != null)
                            _harmony.Patch(trig,
                                prefix: Patch(self, "MemOptimizePrefix"));
                        else Log("内存优化探针未找到 TriggerOptimize()");

                    }
                }
                catch (Exception e) { Log("内存优化探针挂载失败：" + e.Message); }

                MethodInfo scRescan = typeof(SuperController).GetMethod(
                    "RescanPackages",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, Type.EmptyTypes, null);
                if (scRescan != null) PatchStage(scRescan);
                // Incremental VaM-side registration: SuperController.
                // RescanPackages is a thin wrapper over FileManager.Refresh,
                // which rebuilds every package index from scratch (~20s on
                // 10k+ mapped vars) even for a single added package. For a
                // small dirty set we register/unregister just those entries
                // and replay the refresh handlers ourselves.
                try
                {
                    Type fmType = typeof(SuperController).Assembly
                        .GetType("MVR.FileManagement.FileManager");
                    if (fmType != null)
                    {
                        _pkgPathToUid = fmType.GetMethod("packagePathToUid",
                            StaticAll);
                        _fmRegister = fmType.GetMethod("RegisterPackage",
                            StaticAll);
                        _fmUnregister = fmType.GetMethod("UnregisterPackage",
                            StaticAll);
                        _fmByUid = fmType.GetMethod("GetPackagesByUid",
                            StaticAll);
                        _fmRefreshTime = fmType.GetProperty(
                            "lastPackageRefreshTime", StaticAll);
                        _refreshHandlers = fmType.GetField("onRefreshHandlers",
                            StaticAll);
                        FieldInfo pkgFolder = fmType.GetField("packageFolder",
                            StaticAll);
                        _packageFolder = pkgFolder == null
                            ? null : pkgFolder.GetValue(null) as string;
                    }
                }
                catch (Exception e)
                {
                    Log("FileManager 增量注册成员解析失败：" + e.Message);
                    _pkgPathToUid = null;
                }
                Type atomUtils = asm.GetType("JayJayWon.AtomUtils");
                if (scRescan != null && _pkgPathToUid != null &&
                    _fmRegister != null && _fmByUid != null)
                    _harmony.Patch(scRescan,
                        prefix: Patch(self, "ScRescanPrefix"));

                // Incremental local-resource registration members. Every
                // lookup matches name + arity — a name-only GetMethod throws
                // on overloads and would silently disable the whole path.
                try
                {
                    _getResType = FindStatic(resManifest, "GetResourceType", 1);
                    _getPresetType = FindStatic(resManifest,
                        "GetPresetAtomType", 3);
                    _regLocal = FindStatic(resManifest,
                        "RegisterLocalResourceOfType", 5);
                    _purgeLocal = FindStatic(resManifest, "PurgeLocalRVGE", 1);
                    _rvgeDictField = resManifest.GetField(
                        "resourceVersionGroupsDict", StaticAll);
                    Type rt = asm.GetType("JayJayWon.ResourceType");
                    Type fb = asm.GetType("JayJayWon.FolderBrowser");
                    Type rc = asm.GetType("JayJayWon.ResourceCategory");
                    Type dfm = asm.GetType("JayJayWon.DataFileManifest");
                    if (fb != null)
                        _getFolder = FindStatic(fb, "GetFolder", 3);
                    if (rc != null)
                    {
                        FieldInfo pf = rc.GetField("preset", StaticAll);
                        if (pf != null) _rcPreset = (int)pf.GetValue(null);
                    }
                    if (dfm != null)
                    {
                        foreach (MethodInfo m in dfm.GetMethods(StaticAll))
                        {
                            if (m.Name ==
                                "RegisterExisitingDFUserLocalResource" &&
                                m.GetParameters().Length == 2)
                                _regExistingUD = m;
                            if (m.Name ==
                                "RegisterLocalResourceUserSaveRequest" &&
                                m.GetParameters().Length == 1)
                                _regUserSaveReq = m;
                        }
                    }
                    if (rt != null)
                        _getResCat = FindStatic(rt, "GetResourceCategory", 1);
                    if (rt != null)
                    {
                        _rtPresetAtom = (int)rt.GetField("presetAtom",
                            StaticAll).GetValue(null);
                        _rtUnknown = (int)rt.GetField("unknown",
                            StaticAll).GetValue(null);
                        _rtClothingItemPresets = (int)rt.GetField(
                            "clothingItemPresets", StaticAll).GetValue(null);
                        _rtHairItemPresets = (int)rt.GetField(
                            "hairItemPresets", StaticAll).GetValue(null);
                        _rtFemaleClothing = (int)rt.GetField(
                            "femaleClothingItems", StaticAll).GetValue(null);
                        _rtMaleClothing = (int)rt.GetField(
                            "maleClothingItems", StaticAll).GetValue(null);
                        _rtFemaleHair = (int)rt.GetField("femaleHair",
                            StaticAll).GetValue(null);
                        _rtMaleHair = (int)rt.GetField("maleHair",
                            StaticAll).GetValue(null);
                    }
                }
                catch (Exception e)
                {
                    Log("本地增量注册成员解析失败：" + e.Message);
                    _regLocal = null;
                }
                // Dedupe the clothing/hair callback rebuild: PostRescanRefresh
                // re-runs it right after the RescanPackages tail already did,
                // ~17-19s each on this library. The second pass only matters
                // when clothing/hair-typed local files changed mid-scan.
                // Overloads are (bool) and (Atom,bool) — there is no
                // parameterless one, so the former Length==0 lookup never
                // matched and this prefix stayed uninstalled. Patch the
                // all-atoms (bool) entry point.
                if (atomUtils != null)
                {
                    _clothingHairCB = FindStatic(atomUtils,
                        "RefreshClothingHairItemCallbacks", typeof(bool));
                    if (_clothingHairCB != null)
                    {
                        _harmony.Patch(_clothingHairCB,
                            prefix: Patch(self, "ClothingHairPrefix"),
                            finalizer: Patch(self, "ClothingHairFinalizer"));
                        PatchStage(_clothingHairCB);
                    }
                }


                // Class gates for the three rebuild primitives. The
                // IL-verified body of RefreshDynamicItems is exactly
                // RefreshDynamicClothes followed by RefreshDynamicHair, so
                // each half is gated on its own content class; the morph
                // banks get the same verdict and, when a morph-bearing
                // package really was added, are refilled incrementally
                // instead of wiping and re-reading the whole library.
                try
                {
                    ResolveClassGateMembers();
                    if (_dynClothes != null)
                        _harmony.Patch(_dynClothes,
                            prefix: Patch(self, "DynamicClothesPrefix"),
                            finalizer: Patch(self, "DynamicClothesFinalizer"));
                    if (_dynHair != null)
                        _harmony.Patch(_dynHair,
                            prefix: Patch(self, "DynamicHairPrefix"),
                            finalizer: Patch(self, "DynamicHairFinalizer"));
                    if (_dcsInitClothes != null)
                        _harmony.Patch(_dcsInitClothes,
                            prefix: Patch(self, "InitClothesPrefix"),
                            finalizer: Patch(self, "InitClothesFinalizer"));
                    if (_dynUiResync != null)
                        _harmony.Patch(_dynUiResync,
                            prefix: Patch(self, "DynUiResyncPrefix"),
                            finalizer: Patch(self, "DynUiResyncFinalizer"));
                    if (_morphBankRefresh != null)
                        _harmony.Patch(_morphBankRefresh,
                            prefix: Patch(self, "MorphBankPrefix"),
                            finalizer: Patch(self, "MorphBankFinalizer"));
                    Log("类别门控：服装" + (_dynClothes != null ? "" : "×") +
                        " 头发" + (_dynHair != null ? "" : "×") +
                        " 形变" + (_morphBankRefresh != null ? "" : "×") +
                        " 形变增量" +
                        (_fmFindVarDirs != null && _morphImportDir != null &&
                         _morphSubBankComplete != null &&
                         _morphRebuildLookups != null && _getSubBanks != null &&
                         _morphBankAutoFolder != null
                            ? "可用" : "不可用") +
                        " 形变增量移除" +
                        (_morphPkgUidField != null &&
                         _morphStartValueField != null &&
                         _morphValueProp != null &&
                         _subPackageMorphsProp != null &&
                         _subBankRebuildDicts != null
                            ? "可用" : "不可用"));
                }
                catch (Exception e) { Log("类别门控挂载失败：" + e.Message); }
                // BA VFS-aware file lister, used to classify a package
                // diff (clothing/hair/morph content) without extra IO of
                // our own. Missing piece only makes the classifier
                // conservative, never wrong.
                try
                {
                    _utilsType = asm.GetType("JayJayWon.Utils");
                    if (_utilsType != null)
                        _utilsListFiles = FindStatic(_utilsType,
                            "GetFilesAtPathRecursive", typeof(string),
                            typeof(string), typeof(bool), typeof(bool),
                            typeof(bool), typeof(bool));
                    if (_utilsListFiles == null)
                        Log("包内容清单接口缺失，差异判定走保守策略");
                }
                catch (Exception e)
                {
                    _utilsListFiles = null;
                    Log("包内容清单接口解析失败：" + e.Message);
                }

                // Quick-rescan members — per-package manifest update for
                // the VR "快速扫描" action. Missing pieces only degrade the
                // quick path; the coroutine scan is unaffected.
                try
                {
                    _vpmeType = asm.GetType("JayJayWon.VARPackageManifestEntry");
                    _vpvgeType = asm.GetType(
                        "JayJayWon.VARPackageVersionGroupEntry");
                    _vpmeFromPath = FindStatic(varManifest, "GetVPMEFromPath",
                        typeof(string), typeof(bool));
                    _vpmeByKey = FindStatic(varManifest, "GetVPME",
                        typeof(string));
                    _vpmeKeyFromFile = FindStatic(varManifest, "GetVPMEKey",
                        typeof(string));
                    _vpmeKeyOf = _vpmeType == null ? null : FindStatic(
                        varManifest, "GetVPMEKey", _vpmeType);
                    _vpvgeKeyOf = _vpmeType == null ? null : FindStatic(
                        varManifest, "GetVPVGEKey", _vpmeType);
                    _refreshManifestNew = FindStatic(varManifest,
                        "RefreshManifestWithNewVARs", 3);
                    _refreshVarPrefs = FindStatic(varManifest,
                        "RefreshVARFilePrefs", 0);
                    _procMetaDepsAll = FindStatic(varManifest,
                        "ProcessVARMetaFilesAndDependencies", 2);
                    _refreshCombiTags = FindStatic(varManifest,
                        "RefreshAllVARDependencyCombiTags", 0);
                    _cachePresetRvge = FindStatic(resManifest,
                        "CachePrimaryClothingItemPresetRVGEs", 0);
                    _varGroupsProp = varManifest.GetProperty(
                        "varPackageVersionGroups", StaticAll);
                    if (_vpmeType != null)
                    {
                        _vpmeSetNA = _vpmeType.GetMethod("SetNotBAAvailable",
                            BindingFlags.Instance | BindingFlags.Public |
                            BindingFlags.NonPublic);
                        _vpmeVersion = _vpmeType.GetField("varVersion",
                            BindingFlags.Instance | BindingFlags.Public |
                            BindingFlags.NonPublic);
                        if (_vpmeVersion == null)
                            _vpmeVersionProp = _vpmeType.GetProperty(
                                "varVersion", BindingFlags.Instance |
                                BindingFlags.Public | BindingFlags.NonPublic);
                    }
                    if (_vpvgeType != null)
                    {
                        _vpvgeSetMissing = _vpvgeType.GetMethod("SetVARMissing",
                            BindingFlags.Instance | BindingFlags.Public |
                            BindingFlags.NonPublic, null,
                            new[] { typeof(int), typeof(bool) }, null);
                        _vpvgeResPaths = _vpvgeType.GetMethod("GetResourcePaths",
                            BindingFlags.Instance | BindingFlags.Public |
                            BindingFlags.NonPublic, null,
                            new[] { typeof(int) }, null);
                    }
                    if (_vpmeType != null)
                        _vpmeVpvge = _vpmeType.GetProperty("vpvge",
                            BindingFlags.Instance | BindingFlags.Public |
                            BindingFlags.NonPublic);
                }
                catch (Exception e)
                {
                    Log("轻扫成员解析失败：" + e.Message);
                    _vpmeFromPath = null;
                }

                LoadCache();
                SeedKindMemory();
                _installed = true;
                Log("已挂载增量重扫补丁（VAR " + (_varCache != null ? _varCache.Count : 0) +
                    " 条 / 本地缓存 " + (_localCacheSet ? "有" : "无") +
                    " / 类别记忆 " + (_varKindCache != null ? _varKindCache.Count : 0) +
                    " 条）");
            }
            catch (Exception e) { Fail("安装异常 " + e.Message); }
        }

        private static HarmonyMethod Patch(Type self, string name)
        {
            return new HarmonyMethod(self.GetMethod(name, StaticAll));
        }

        private static MethodInfo FindStatic(Type host, string name,
            int paramCount)
        {
            foreach (MethodInfo m in host.GetMethods(StaticAll))
                if (m.Name == name &&
                    m.GetParameters().Length == paramCount)
                    return m;
            return null;
        }

        private static MethodInfo FindStatic(Type host, string name,
            params Type[] sig)
        {
            foreach (MethodInfo m in host.GetMethods(StaticAll))
            {
                if (m.Name != name) continue;
                var ps = m.GetParameters();
                if (ps.Length != sig.Length) continue;
                bool ok = true;
                for (int i = 0; i < ps.Length; i++)
                    if (ps[i].ParameterType != sig[i]) { ok = false; break; }
                if (ok) return m;
            }
            return null;
        }

        private static void PatchStage(Type host, string name)
        {
            try
            {
                MethodInfo m = null;
                foreach (MethodInfo c in host.GetMethods(StaticAll))
                    if (c.Name == name) { m = c; break; }
                if (m == null) { Log("探针未找到 " + host.Name + "." + name); return; }
                PatchStage(m);
            }
            catch (Exception e) { Log("探针挂载失败 " + name + ": " + e.Message); }
        }

        private static void PatchStage(MethodInfo m)
        {
            try
            {
                _harmony.Patch(m,
                    prefix: Patch(typeof(BrowserAssistScanAccelerator), "StagePrefix"),
                    finalizer: Patch(typeof(BrowserAssistScanAccelerator), "StageFinalizer"));
            }
            catch (Exception e) { Log("探针挂载失败 " + m.Name + ": " + e.Message); }
        }

        private static void StagePrefix(MethodBase __originalMethod)
        {
            _stageSw = System.Diagnostics.Stopwatch.StartNew();
            Log("stage → " + __originalMethod.DeclaringType.Name + "." + __originalMethod.Name);
        }

        private static Exception StageFinalizer(MethodBase __originalMethod, Exception __exception)
        {
            Log("stage ← " + __originalMethod.DeclaringType.Name + "." + __originalMethod.Name +
                " " + (_stageSw != null ? _stageSw.ElapsedMilliseconds : 0) + "ms" +
                (__exception != null ? " 异常:" + __exception.GetType().Name : ""));
            return __exception;
        }

        // ---------- patch: SuperController.RescanPackages ----------
        // Fires only inside a fingerprinted scan (_varDirty != null). A small
        // dirty set registers incrementally via FileManager's public
        // Register/UnregisterPackage; large sets fall back to the native
        // full Refresh (index rebuild is more robust at scale).

        private static bool ScRescanPrefix()
        {
            if (!Enabled || _varDirty == null || _pkgPathToUid == null ||
                _varDirty.Count == 0 || _varDirty.Count > MaxIncrementalPkgs)
                return true;
            try
            {
                // Skip the scene-bound FileManager handlers here: the
                // per-atom RefreshDynamicItems / RefreshPackageMorphs they
                // replay are the same work the clothing-hair callback pass
                // does, and a 3-atom scene pays them 3x (~40s). The single
                // rebuild stays where BA puts it (RescanPackages tail), so
                // the ordering is unchanged; the gates below only stop the
                // duplicates and skip it when the diff has nothing for it.
                if (_removedCount > 0 && !IncrementalRemoval) return true;
                if (!RunVarIncrementalRegistration(_varDirty, true))
                    return true;
                return false;
            }
            catch (Exception e)
            {
                Log("VaM 增量注册异常，回退原生全量：" + e.Message);
                return true;
            }
        }

        // Decides, once per fingerprinted diff, which scene-side rebuild
        // classes have anything to do and which of them the whole pass must
        // still run for. Two levels are kept apart on purpose:
        //   _sceneRebuildNeeded — the historical union (IsSceneContentPath):
        //       any clothing / hair / morph *or preset* entry. It still gates
        //       the pass as a whole, so BA's clothing-item-preset recount and
        //       the clothing:* callback re-hook are untouched.
        //   _needClothing/_needHair/_needMorphs — only the paths the three
        //       heavy primitives actually read, so e.g. a package that only
        //       ships clothing presets re-hooks callbacks but does not re-sync
        //       the item catalogs.
        // Anything we cannot prove harmless (removed package, unlistable
        // package, BA temp var) counts as all three, so the conservative path
        // stays the default. Entry names come from BaDirtyContentProbe: the
        // package archive is read directly first (central directory), the BA
        // VFS lister is only a fallback, and an empty result from both is
        // treated as unavailable instead of harmless.
        private static void ClassifyDirtyContent(HashSet<string> dirty)
        {
            _sceneRebuildDecided = true;
            SnapshotPassSelectors();
            _needClothing = _needHair = _needMorphs = false;
            _needClothingMale = false;
            _pkgClothing = _pkgHair = _pkgMorphs = 0;
            _sceneRebuildNeeded = false;
            _sceneRebuildWhy = "无包文件变更";
            if (dirty == null || dirty.Count == 0) return;
            int removed = 0, unlisted = 0, none = 0, presetOnly = 0;
            var removedPaths = new List<string>();
            bool anyScene = false;
            foreach (string abs in dirty)
            {
                try
                {
                    bool exists = File.Exists(abs);
                    bool disabled = File.Exists(abs + ".disabled");
                    if (!exists && !disabled)
                    {
                        removed++;
                        removedPaths.Add(abs);
                        continue;
                    }
                    if (abs.EndsWith(".var.batempvar",
                            StringComparison.OrdinalIgnoreCase))
                    { unlisted++; continue; }
                    List<string> files =
                        BaDirtyContentProbe.PackageEntries(abs);
                    if (files == null || files.Count == 0)
                    { unlisted++; continue; }
                    bool pkgScene = false;
                    for (int n = 0; n < files.Count; n++)
                        if (IsSceneContentPath(files[n])) { pkgScene = true; break; }
                    bool c = false, h = false, m = false, b = false;
                    KindFlagsOf(files, ref c, ref h, ref m, ref b);
                    if (b) _needClothingMale = true;
                    RememberVarKind(abs, c, h, m, b);
                    if (!pkgScene) { none++; continue; }
                    anyScene = true;
                    if (c) _pkgClothing++;
                    if (h) _pkgHair++;
                    if (m) _pkgMorphs++;
                    if (!c && !h && !m) presetOnly++;
                    _needClothing |= c;
                    _needHair |= h;
                    _needMorphs |= m;
                }
                catch { unlisted++; }
            }
            // A removed package can drop items out of a list without
            // telling us which class it fed — unless the package was listed
            // while it still existed, which is what _varKindCache records.
            // A package with a memory verdict only invalidates the classes
            // it actually contributed; one with no memory, and an
            // unlistable package (still present), stay on the conservative
            // all-three path. Morphs never take the memory shortcut — that
            // rebuild wipes and re-imports every bank's whole package tree
            // — so the banks themselves are asked whether the removed uid
            // was ever loaded there.
            bool removedMorphs = false;
            int kindKnown = 0, kindUnknown = 0;
            bool memClothing = false, memHair = false, memMale = false;
            if (removed > 0 || unlisted > 0)
            {
                for (int i = 0; i < removedPaths.Count; i++)
                {
                    string kind;
                    if (_varKindCache == null ||
                        !_varKindCache.TryGetValue(removedPaths[i], out kind))
                    { kindUnknown++; continue; }
                    kindKnown++;
                    if (kind.IndexOf('c') >= 0) memClothing = true;
                    if (kind.IndexOf('h') >= 0) memHair = true;
                    if (kind.IndexOf('b') >= 0) memMale = true;
                }
                bool unknownContribution = unlisted > 0 || kindUnknown > 0;
                _needClothing |= unknownContribution || memClothing;
                _needHair |= unknownContribution || memHair;
                _needClothingMale |= unknownContribution || memMale;
                removedMorphs = unlisted > 0 ||
                    RemovedLoadedInMorphBanks(removedPaths);
                if (removedMorphs) _needMorphs = true;
                if (removed > 0)
                    Log("类别记忆：移除 " + removed + " 包，命中 " + kindKnown +
                        " 未命中 " + kindUnknown + "；按记忆 服装" +
                        (memClothing ? "有" : "无") + " 头发" +
                        (memHair ? "有" : "无") + " 男装" +
                        (memMale ? "有" : "无"));
            }
            _removedCount = removed;
            if (removed > 0)
                _sceneRebuildWhy = removed + " 个包被移除（" +
                    (kindUnknown > 0
                        ? kindUnknown + " 个无类别记忆，服装/头发随动"
                        : "按类别记忆：服装" + (memClothing ? "有" : "无") +
                          "、头发" + (memHair ? "有" : "无")) +
                    "，形变" + (removedMorphs ? "需重建" : "不受影响") + "）";
            else if (unlisted > 0)
                _sceneRebuildWhy = unlisted + " 个包内容清单不可用（类别未知）";
            else if (!anyScene)
                _sceneRebuildWhy = dirty.Count + " 个包均不含服装/头发/形变";
            else
            {
                var parts = new List<string>();
                if (_pkgClothing > 0)
                    parts.Add("服装项 " + _pkgClothing + " 包");
                if (_pkgHair > 0)
                    parts.Add("头发项 " + _pkgHair + " 包");
                if (_pkgMorphs > 0)
                    parts.Add("形变 " + _pkgMorphs + " 包");
                if (presetOnly > 0)
                    parts.Add("仅服装/头发预设 " + presetOnly + " 包");
                _sceneRebuildWhy = string.Join("、", parts.ToArray());
            }
            _sceneRebuildNeeded = anyScene || removed > 0 || unlisted > 0;
            Log("差异内容判定：" + (_sceneRebuildNeeded ? "需要场景重建" :
                "无需场景重建") + " — " + _sceneRebuildWhy +
                "（服装 " + (_needClothing ? "√" : "×") + " 头发 " +
                (_needHair ? "√" : "×") + " 形变 " +
                (_needMorphs ? "√" : "×") + "）");
        }
        // Category verdict of one package's entry list. Shared by the
        // per-pass classifier and the install-time seeder so the two can
        // never disagree about the same package.
        private static void KindFlagsOf(List<string> files, ref bool clothing,
            ref bool hair, ref bool morphs, ref bool male)
        {
            for (int i = 0; i < files.Count; i++)
            {
                string f = files[i];
                bool fc = false, fh = false, fm = false;
                ContentClassOf(f, ref fc, ref fh, ref fm);
                if (fc)
                {
                    clothing = true;
                    if (IsMaleClothingPath(f)) male = true;
                }
                if (fh) hair = true;
                if (fm) morphs = true;
            }
        }

        // Writes the category verdict of one listed package. Char-based so
        // the persisted map stays tiny (one short string per var file).
        private static void RememberVarKind(string abs, bool clothing,
            bool hair, bool morphs, bool male)
        {
            if (string.IsNullOrEmpty(abs)) return;
            try
            {
                if (_varKindCache == null)
                    _varKindCache = new Dictionary<string, string>(
                        StringComparer.OrdinalIgnoreCase);
                var sb = new System.Text.StringBuilder(4);
                if (clothing) sb.Append('c');
                if (male) sb.Append('b');
                if (hair) sb.Append('h');
                if (morphs) sb.Append('m');
                _varKindCache[abs] = sb.ToString();
            }
            catch { }
        }

        // One package file list through BA own VFS-aware lister — the same
        // call the native per-package rescan uses. null = unavailable, and
        // the caller then stays conservative.
        internal static List<string> ListPackageFiles(string absVarPath)
        {
            if (_utilsListFiles == null) return null;
            try
            {
                string cwd = null;
                string rel = ToRelative(absVarPath, ref cwd) + ":";
                object res = _utilsListFiles.Invoke(null, new object[]
                    { rel, "*.*", false, false, true, false });
                var set = res as System.Collections.IEnumerable;
                if (set == null) return null;
                var list = new List<string>();
                foreach (object o in set)
                {
                    string s = o as string;
                    if (s != null) list.Add(s);
                }
                return list;
            }
            catch { return null; }
        }

        private static bool IsSceneContentPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string p = path.Replace('/', '\\').ToLowerInvariant();
            if (p.EndsWith(".vmi") || p.EndsWith(".vmb")) return true;
            if (p.IndexOf("\\clothing\\", StringComparison.Ordinal) >= 0)
                return true;
            if (p.IndexOf("\\hair\\", StringComparison.Ordinal) >= 0)
                return true;
            if (p.IndexOf("\\person\\morphs\\", StringComparison.Ordinal) >= 0)
                return true;
            return false;
        }

        // True only inside the refresh pass that produced the verdict, so a
        // user action (wearing an item triggers BA own callback refresh) is
        // never affected by a stale decision.
        private static bool SkipSceneRebuildForThisPass()
        {
            if (!_sceneRebuildDecided || _sceneRebuildNeeded ||
                _localClothingHairDirty) return false;
            return CalledFromGatedPath();
        }

        private static bool RunVarIncrementalRegistration(
            HashSet<string> dirty, bool skipSceneHandlers)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var byUid = _fmByUid.Invoke(null, null)
                as System.Collections.IDictionary;
            string cwd = null;
            int added = 0, reloaded = 0, removed = 0, skipped = 0;
            int unresolved = 0;
            foreach (string path in dirty)
            {
                try
                {
                    bool exists = File.Exists(path);
                    // VaM only indexes packages under packageFolder —
                    // offloaded vars stay outside its registry.
                    if (exists && !UnderPackageFolder(path))
                    { skipped++; continue; }
                    string rel = ToRelative(path, ref cwd);
                    string uid = _pkgPathToUid.Invoke(null,
                        new object[] { rel }) as string;
                    object pkg = uid == null || byUid == null
                        ? null : byUid[uid];
                    if (pkg != null)
                    {
                        _fmUnregister.Invoke(null, new[] { pkg });
                        removed++;
                    }
                    else if (!exists)
                    {
                        // The path VaM registered for this archive is
                        // unknown, so it would stay registered and keep
                        // feeding the catalogs: hand the pass back to the
                        // native path instead of half-applying a removal.
                        unresolved++;
                    }
                    if (exists)
                    {
                        var psw = System.Diagnostics.Stopwatch.StartNew();
                        _fmRegister.Invoke(null, new object[] { rel });
                        if (psw.ElapsedMilliseconds > 200)
                            Log("单包注册慢 " + psw.ElapsedMilliseconds +
                                "ms " + rel);
                        if (pkg != null) reloaded++; else added++;
                    }
                }
                catch (Exception e)
                {
                    skipped++;
                    Log("单包注册失败 " + path + ": " + e.Message);
                }
            }
            long loopMs = sw.ElapsedMilliseconds;
            if (_fmRefreshTime != null)
                try { _fmRefreshTime.SetValue(null, DateTime.Now, null); }
                catch { }
            if (unresolved > 0)
            {
                Log("VaM 增量注册：移除时 " + unresolved +
                    " 个包未找到已注册 uid，回退原生全量");
                return false;
            }
            InvokeRefreshHandlers(skipSceneHandlers);
            Log("VaM 增量注册：新增 " + added + " 重载 " + reloaded +
                " 移除 " + (removed - reloaded) + " 跳过 " + skipped +
                "，注册 " + loopMs + "ms + 回调 " +
                (sw.ElapsedMilliseconds - loopMs) + "ms");
            return true;
        }

        private static bool UnderPackageFolder(string absPath)
        {
            try
            {
                string folder = string.IsNullOrEmpty(_packageFolder)
                    ? "AddonPackages" : _packageFolder;
                string abs = Norm(Path.Combine(
                    Directory.GetCurrentDirectory(), folder));
                return absPath.StartsWith(abs + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase);
            }
            catch { return true; }
        }

        private static string ToRelative(string absPath, ref string cwd)
        {
            if (cwd == null) cwd = Norm(Directory.GetCurrentDirectory());
            if (absPath.StartsWith(cwd + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
                return absPath.Substring(cwd.Length + 1);
            return absPath;
        }

        private static void InvokeRefreshHandlers(bool skipSceneHandlers)
        {
            try
            {
                var handlers = _refreshHandlers.GetValue(null) as Delegate;
                if (handlers == null) return;
                int skipped = 0, pkgSkipped = 0;
                foreach (Delegate d in handlers.GetInvocationList())
                {
                    // Scene-bound handlers rebuild per-atom morph lists and
                    // clothing item selectors — the exact work the quick
                    // scan exists to avoid (~30s in-scene). VaM's package
                    // list handlers (e.g. PackageBuilder.SyncPackages)
                    // still run so native UIs stay consistent.
                    string handlerOwner = d.Method.DeclaringType == null
                        ? null : d.Method.DeclaringType.Name;
                    if (skipSceneHandlers && handlerOwner != null &&
                        handlerOwner.StartsWith("DAZCharacter"))
                    { skipped++; continue; }
                    // PackageBuilder.SyncPackages re-syncs VaM Package
                    // Builder list (~13s on an 11k-var library) and has
                    // nothing to do with the incremental registration just
                    // performed — VaM authoritative package registry is
                    // MVR.FileManagement.FileManager, already updated per
                    // package above. [BA]SkipPackageListSync=false restores.
                    if (skipSceneHandlers && SkipPackageListSync &&
                        handlerOwner != null &&
                        handlerOwner.StartsWith("PackageBuilder"))
                    { pkgSkipped++; continue; }
                    var hsw = System.Diagnostics.Stopwatch.StartNew();
                    try { d.DynamicInvoke(); }
                    catch (Exception e)
                    {
                        Log("刷新回调异常 " + d.Method.DeclaringType.Name +
                            "." + d.Method.Name + ": " + e.Message);
                    }
                    if (hsw.ElapsedMilliseconds > 50)
                        Log("刷新回调 " + d.Method.DeclaringType.Name + "." +
                            d.Method.Name + " " + hsw.ElapsedMilliseconds + "ms");
                }
                if (skipped > 0)
                    Log("轻扫跳过场景回调 " + skipped +
                        " 个（原子服装/形变列表留待完整重扫）");
                if (pkgSkipped > 0)                     Log("跳过包列表回调 " + pkgSkipped +                         " 个（PackageBuilder.SyncPackages）");
            }
            catch (Exception e) { Log("刷新回调枚举失败：" + e.Message); }
        }

        private static void Fail(string why)
        {
            _failed = true;
            Log("增量重扫未启用：" + why);
        }

        // ---------- quick rescan (VR quick action) ----------
        // Fingerprint diff + incremental registration only — the whole
        // point is that it NEVER enumerates scene atoms: no
        // RefreshDynamicItems / RefreshPackageMorphs / clothing-hair
        // callback rebuild. BA's manifest stays in sync (vpmes, rvges,
        // prefs, user data) so the browser shows the change. When no
        // baseline exists or the diff is oversized we decline rather than
        // silently run the heavy native path — the full BA rescan button
        // remains the fallback.

        internal static void QuickRescan()
        {
            if (!Enabled || !_installed)
            { Log("轻扫：增量补丁未启用，请用 BA 完整重扫"); return; }
            if (_inQuickScan) { Log("轻扫：上一趟还在跑"); return; }
            _inQuickScan = true;
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    if (MemoryOptimizer.singleton != null)
                        MemoryOptimizer.singleton.TriggerOptimize();
                }
                catch { }
                QuickVarScan();
                QuickLocalScan();
                if (_localClothingHairDirty) _quickScanSawCH = true;
                if (_postRescan != null)
                    try { _postRescan.Invoke(null, null); }
                    catch (Exception e)
                    { Log("轻扫刷新浏览器列表失败：" + e.Message); }
                Log("轻扫完成，总耗时 " + sw.ElapsedMilliseconds + "ms");
                if (_quickScanSawCH)
                    Log("轻扫：检测到服装/头发资源——「快速扫描▸服装/头发」" +
                        "可按需刷新场景人物列表");
            }
            finally
            {
                _inQuickScan = false;
                _varDirty = null;
            }
        }

        // The scene-side refresh a quick scan deliberately skips — exposed
        // as the 服装/头发 sub-button so the per-atom callback rebuild
        // (~17-19s on this library) is paid only when new clothing/hair
        // resources actually arrived and the user wants them in-scene.
        internal static void RefreshClothingHair()
        {
            if (!_installed || _clothingHairCB == null)
            { Log("服装/头发刷新：接口不可用"); return; }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Log("服装/头发刷新：重建场景人物回调…");
            try { _clothingHairCB.Invoke(null, new object[] { false }); }
            catch (Exception e)
            { Log("服装/头发刷新失败：" + e.Message); return; }
            _quickScanSawCH = false;
            Log("服装/头发刷新完成 " + sw.ElapsedMilliseconds + "ms");
        }

        internal static bool ClothingHairPending
        {
            get { return _quickScanSawCH; }
        }

        private static void QuickVarScan()
        {
            _truncated = false;
            Dictionary<string, string> cur;
            try { cur = VarFingerprint(); }
            catch (Exception e)
            { Log("轻扫 VAR 指纹异常：" + e.Message); return; }
            if (_truncated)
            { Log("轻扫 VAR：指纹超限，请用 BA 完整重扫"); return; }
            int pop = -1;
            try { pop = CollectionCount(_availableVars.GetValue(null, null)); }
            catch { }
            if (_varCache == null || pop <= 0)
            {
                Log("轻扫 VAR：无可用基线或清单为空，请先用 BA 完整重扫一次");
                return;
            }
            bool anyDiff = !Equal(cur, _varCache);
            if (!anyDiff)
            { Log("轻扫 VAR：指纹 " + cur.Count + " 条目无变化"); return; }
            HashSet<string> dirty = DirtyVarPaths(cur, _varCache);
            if (dirty.Count > MaxIncrementalPkgs)
            {
                Log("轻扫 VAR：脏集 " + dirty.Count +
                    " 个超过增量上限，请用 BA 完整重扫");
                return;
            }
            Log("轻扫 VAR：" + dirty.Count + " 个包新增/变更");
            _varDirty = dirty;
            _varCache = cur;   // early commit — same crash rationale
            SaveCache();
            if (dirty.Count > 0)
            {
                if (_pkgPathToUid != null && _fmRegister != null)
                    try { RunVarIncrementalRegistration(dirty, true); }
                    catch (Exception e)
                    { Log("轻扫 VaM 侧注册失败：" + e.Message); }
                else Log("轻扫 VaM 侧注册不可用");
                UpdateVarManifest(dirty);
            }
            else
                Log("轻扫 VAR：仅偏好/管理状态变化，无包文件变更");
            // Prefs (disable/enable state) change without the .var file
            // itself diffing — refresh them whenever anything moved.
            if (_refreshVarPrefs != null)
                try { _refreshVarPrefs.Invoke(null, null); }
                catch (Exception e)
                { Log("轻扫 VAR prefs 刷新失败：" + e.Message); }
            if (_refreshCombiTags != null)
                try { _refreshCombiTags.Invoke(null, null); }
                catch { }
        }

        private static void UpdateVarManifest(HashSet<string> dirty)
        {
            if (_vpmeFromPath == null || _refreshManifestNew == null ||
                _vpmeType == null)
            { Log("轻扫 VAR：BA 清单接口缺失，浏览器列表可能滞后"); return; }
            try
            {
                // Build Dictionary<string,VARPackageManifestEntry> for the
                // touched files only — RefreshManifestWithNewVARs processes
                // exactly the set it is handed.
                var touched = (System.Collections.IDictionary)
                    Activator.CreateInstance(
                        typeof(Dictionary<,>).MakeGenericType(
                            typeof(string), _vpmeType));
                string cwd = null;
                int removed = 0, touchedCount = 0;
                foreach (string abs in dirty)
                {
                    try
                    {
                        string rel = ToRelative(abs, ref cwd);
                        bool exists = File.Exists(abs);
                        bool disabled = File.Exists(abs + ".disabled");
                        if (!exists && !disabled)
                        {   // truly gone → mark missing
                            MarkVarMissing(rel);
                            removed++;
                            continue;
                        }
                        // A vanished .var whose .disabled twin exists is a
                        // disable, not a removal — GetVPMEFromPath records
                        // the flag via SetDisabled.
                        object vpme = _vpmeFromPath.Invoke(null,
                            new object[] { rel, disabled || !exists });
                        if (vpme == null) continue;
                        string key = _vpmeKeyOf == null ? null :
                            _vpmeKeyOf.Invoke(null, new[] { vpme })
                                as string;
                        if (key == null) key = rel;
                        if (!touched.Contains(key)) touched.Add(key, vpme);
                        touchedCount++;
                    }
                    catch (Exception e)
                    {
                        Log("轻扫 VAR 清单项失败 " + abs + "：" + e.Message);
                    }
                }
                object enabled = _refreshManifestNew.Invoke(null,
                    new object[]
                    { touched, DateTime.Now, new HashSet<int>() });
                if (_cachePresetRvge != null)
                    try { _cachePresetRvge.Invoke(null, null); }
                    catch { }
                if (_procMetaDepsAll != null && enabled != null)
                    try
                    {
                        _procMetaDepsAll.Invoke(null,
                            new object[] { enabled, false });
                    }
                    catch (Exception e)
                    { Log("轻扫 meta 依赖处理失败：" + e.Message); }
                // Only now (post-refresh) do new packages expose their
                // resource paths — check whether the touched set carries
                // clothing/hair so the optional scene refresh is suggested
                // only when it would do something.
                if (!_quickScanSawCH)
                    foreach (System.Collections.DictionaryEntry de in touched)
                        if (VpmeContainsClothingHair(de.Value))
                        { _quickScanSawCH = true; break; }
                Log("轻扫 VAR 清单：更新 " + touchedCount + " 移除 " +
                    removed);
            }
            catch (Exception e)
            {
                Log("轻扫 VAR 清单更新失败：" + e.Message);
            }
        }

        private static void MarkVarMissing(string relPath)
        {
            if (_vpmeKeyFromFile == null || _vpmeByKey == null ||
                _vpmeSetNA == null || _vpvgeSetMissing == null ||
                _vpvgeKeyOf == null || _varGroupsProp == null)
                return;
            try
            {
                string key = _vpmeKeyFromFile.Invoke(null,
                    new object[] { Path.GetFileName(relPath) }) as string;
                if (key == null) return;
                object vpme = _vpmeByKey.Invoke(null, new object[] { key });
                if (vpme == null) return;
                // Removed clothing/hair still needs the scene list refresh —
                // the vpvge keeps its registered paths until purged.
                if (VpmeContainsClothingHair(vpme)) _quickScanSawCH = true;
                _vpmeSetNA.Invoke(vpme, null);
                string gkey = _vpvgeKeyOf.Invoke(null, new[] { vpme })
                    as string;
                var groups = _varGroupsProp.GetValue(null, null)
                    as System.Collections.IDictionary;
                if (groups == null || gkey == null ||
                    !groups.Contains(gkey)) return;
                int ver = _vpmeVersion != null
                    ? (int)_vpmeVersion.GetValue(vpme)
                    : (int)_vpmeVersionProp.GetValue(vpme, null);
                _vpvgeSetMissing.Invoke(groups[gkey],
                    new object[] { ver, true });
            }
            catch (Exception e)
            { Log("轻扫标记缺失失败 " + relPath + "：" + e.Message); }
        }

        private static bool VpmeContainsClothingHair(object vpme)
        {
            try
            {
                if (vpme == null || _vpmeVpvge == null ||
                    _vpvgeResPaths == null ||
                    (_vpmeVersion == null && _vpmeVersionProp == null))
                    return false;
                object vpvge = _vpmeVpvge.GetValue(vpme, null);
                if (vpvge == null) return false;
                int ver = _vpmeVersion != null
                    ? (int)_vpmeVersion.GetValue(vpme)
                    : (int)_vpmeVersionProp.GetValue(vpme, null);
                var paths = _vpvgeResPaths.Invoke(vpvge,
                    new object[] { ver }) as System.Collections.IEnumerable;
                if (paths == null) return false;
                foreach (object p in paths)
                {
                    string rp = (p as string ?? "").Replace('/', '\\');
                    if (rp.StartsWith("Custom\\Clothing\\",
                            StringComparison.OrdinalIgnoreCase) ||
                        rp.StartsWith("Custom\\Hair\\",
                            StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            catch { }
            return false;
        }

        private static void QuickLocalScan()
        {
            var fileMap = new Dictionary<string, long>(
                StringComparer.OrdinalIgnoreCase);
            _truncated = false;
            long[] hashes, counts;
            try { LocalFingerprint(out hashes, out counts, fileMap); }
            catch (Exception e)
            { Log("轻扫本地指纹异常：" + e.Message); return; }
            if (_truncated)
            { Log("轻扫本地：指纹超限，请用 BA 完整重扫"); return; }
            int pop = -1;
            try { pop = CollectionCount(_localRvges.GetValue(null)); }
            catch { }
            bool same = EqualLongs(hashes, _localHashCache) &&
                EqualLongs(counts, _localCountCache);
            if (_localCacheSet && pop > 0 && same)
            {
                Log("轻扫本地：指纹无变化");
                _localClothingHairDirty = false;
                return;
            }
            var baseline = _localFileCache;
            if (pop > 0 && baseline != null &&
                TryIncrementalLocalUpdate(fileMap, baseline))
            {
                _localHashCache = hashes; _localCountCache = counts;
                _localFileCache = fileMap;
                _localCacheSet = true;
                SaveCache();
                return;
            }
            // Quick mode never runs the native clear-and-rescan: leave the
            // cache untouched so the diff stays pending for the next scan.
            if (baseline == null)
                Log("轻扫本地：无逐文件基线，请先用 BA 完整重扫一次");
            else
                Log("轻扫本地：差异超出增量上限或处理失败，" +
                    "本地清单未刷新——请用 BA 完整重扫");
        }

        // ---------- patch: RescanAllVARResources ----------

        private static bool VarAllPrefix()
        {
            _inVarAll++;
            ClearFafMemo();
            _chRebuildRan = false;
            _sceneRebuildDecided = false;
            _sceneRebuildNeeded = false;
            _sceneRebuildWhy = null;            ResetPassAccounting();

            if (!Enabled) return true;
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                _truncated = false;
                Dictionary<string, string> cur = VarFingerprint();
                if (_truncated)
                {
                    _varPending = null; _varDirty = null;
                    Log("VAR 指纹超限（>" + MaxEntries + " 条），回退原生全量");
                    return true;
                }
                int pop = -1;
                try { pop = CollectionCount(_availableVars.GetValue(null, null)); }
                catch { }
                bool skip = _varCache != null && pop > 0 && Equal(cur, _varCache);
                string why = skip ? "无变化" :
                    _varCache == null ? "无缓存" :
                    pop <= 0 ? "VAR清单为空(" + pop + ")" : "有变化";
                Log("VAR 指纹：" + cur.Count + " 条目，耗时 " +
                    sw.ElapsedMilliseconds + "ms，" + why + "；" +
                    LinkCacheStat());
                if (skip)
                {
                    _varPending = null; _varDirty = null;
                    InvokePostRescan();
                    return false;
                }
                _varPending = cur;
                _varDirty = DirtyVarPaths(cur, _varCache);
                ClassifyDirtyContent(_varDirty);
                Log("VAR 重扫：" + _varDirty.Count + " 个包新增/变更，其余 " +
                    Math.Max(0, cur.Count - _varDirty.Count) + " 个跳过包内枚举");
                if (_varCache != null)
                {
                    var diff = new List<string>();
                    foreach (var kv in cur)
                    {
                        string old;
                        if (!_varCache.TryGetValue(kv.Key, out old) ||
                            old != kv.Value)
                        {
                            diff.Add(kv.Key);
                            if (diff.Count >= 8) break;
                        }
                    }
                    foreach (var kv in _varCache)
                    {
                        if (!cur.ContainsKey(kv.Key))
                        {
                            diff.Add("-" + kv.Key);
                            if (diff.Count >= 8) break;
                        }
                    }
                    if (diff.Count > 0)
                        Log("VAR 指纹差异键：" + string.Join("、",
                            diff.ToArray()));
                }
                _varCache = _varPending;
                SaveCache();
                return true;
            }
            catch (Exception e)
            {
                _varPending = null; _varDirty = null;
                Log("VAR 指纹异常，回退原生重扫：" + e.Message);
                return true;
            }
        }

        private static Exception VarAllFinalizer(Exception __exception)
        {
            _inVarAll--;
            // Cache was committed in the prefix; a managed exception here
            // means the manifest may be half-built — invalidate so the next
            // scan repairs instead of skipping.
            if (__exception != null && _varPending != null)
            {
                _varCache = null;
                SaveCache();
            }
            _varPending = null; _varDirty = null;
            SyncItemsCacheEnd();
            LogPassSummary();
            return __exception;
        }

        // One line per fingerprinted pass so a single log read says which
        // scene-side rebuild ran and why (R5).
        private static void LogPassSummary()
        {
            ClearFafMemo();
            if (!_sceneRebuildDecided) return;
            Log("本趟差异小结：" + (_sceneRebuildNeeded
                ? "含场景相关内容" : "无场景相关内容") +
                (_chRebuildRan ? "；场景列表已重建一次" : "；场景列表未重建") +
                " — " + _sceneRebuildWhy);            if (_sceneRebuildDecided)
                Log("重建耗时：服装 " + _nClothes + " 次 " +
                    _msClothes.ToString("F0") + "ms / 头发 " + _nHair +
                    " 次 " + _msHair.ToString("F0") + "ms / 形变 " + _nMorph +
                    " 次 " + _msMorph.ToString("F0") + "ms；按类别跳过 服装 " +
                    _skipClothes + " / 头发 " + _skipHair + " / 形变 " +
                    _skipMorph + (_skipClothesMale > 0
                        ? " / 男装容器 " + _skipClothesMale : "") +
                    (_nInitClothes > 0
                        ? "；容器接口重建 " + _nInitClothes + " 次 " +
                          _msInitClothes.ToString("F0") + "ms（其中服装UI刷新 " +
                          _nResync + " 次 " + _msResync.ToString("F0") +
                          "ms，隐藏面板跳过 " + _skipResyncUi + " 次" +
                          (_skipResyncMaleUi > 0
                              ? "，男装UI跳过 " + _skipResyncMaleUi + " 次"
                              : "") + "）"
                        : "") +
                    (_incMorphPkgs > 0
                        ? "；形变增量导入 " + _incMorphPkgs + " 个包" : "") +
                    (_morphPurgePkgs > 0
                        ? "；形变增量移除 " + _morphPurgePkgs + " 个包 " +
                          _morphPurgeItems + " 项" + (_morphPurgeZeroed > 0
                              ? "（残值归零 " + _morphPurgeZeroed + "）" : "")
                        : ""));
        }

        // ---------- attribution: ScanForVARs internals ----------

        private static void ScanForVarsEnter()
        {
            _inScanForVars = true;
            _sfvSw = System.Diagnostics.Stopwatch.StartNew();
            _sfvEnumTicks = _sfvDirTicks = _sfvVpmeTicks = 0;
            _sfvEnumN = _sfvDirN = _sfvDirHit = _sfvVpmeN = 0;
            _fmsServeN = _fmsNativeN = _fmsStatTicks = 0;
            InvalidateVarProbeMemo();
        }

        private static Exception ScanForVarsExit(Exception __exception)
        {
            _inScanForVars = false;
            Log("ScanForVARs 内部归属：包枚举 " + _sfvEnumN + " 次 " + Ms(_sfvEnumTicks) +
                "ms / 挂载探测 " + _sfvDirN + " 次 " + Ms(_sfvDirTicks) + "ms" +
                " / VPME构建 " + _sfvVpmeN + " 次 " + Ms(_sfvVpmeTicks) + "ms（缓存命中 " +
                _sfvDirHit + "）/ 全量 " +
                (_sfvSw != null ? _sfvSw.ElapsedMilliseconds : 0) + "ms / 目录记忆 命中 " +
                    _fmsServeN + " 原生 " + _fmsNativeN + " 校验 " +
                    Ms(_fmsStatTicks) + "ms 清单 " + _fmsListings.Count);
            _sfvSw = null;
            SaveFmsListings();
            return __exception;
        }

        private static void InvalidateVarProbeMemo()
        {
            if (_sfvDirMemo == null || _sfvDirMemo.Count == 0) return;
            if (_varDirty == null || _varDirty.Count == 0) return;
            var drop = new List<string>();
            foreach (string key in _sfvDirMemo.Keys)
            {
                string n = key;
                if (n.Length > 1 && n[n.Length - 1] == ':')
                    n = n.Substring(0, n.Length - 1);
                try
                {
                    if (_varDirty.Contains(Norm(n))) drop.Add(key);
                }
                catch { }
            }
            for (int i = 0; i < drop.Count; i++) _sfvDirMemo.Remove(drop[i]);
        }

        private static void EnumProbeEnter()
        {
            if (_inScanForVars) _sfvEnumSw = System.Diagnostics.Stopwatch.StartNew();
        }

        private static Exception EnumProbeExit(Exception __exception)
        {
            if (_inScanForVars && _sfvEnumSw != null)
            {
                _sfvEnumTicks += _sfvEnumSw.ElapsedTicks;
                _sfvEnumN++;
                _sfvEnumSw = null;
            }
            return __exception;
        }

        // Only paths that ask "is this archive mounted" and only inside the
        // native scan are memoized; every other caller keeps the native
        // answer.
        private static bool DirProbePrefix(string __0, ref bool __result)
        {
            if (!_inScanForVars || _sfvDirMemo == null) return true;
            if (string.IsNullOrEmpty(__0) || __0[__0.Length - 1] != ':') return true;
            bool cached;
            if (_sfvDirMemo.TryGetValue(__0, out cached))
            {
                _sfvDirHit++;
                _sfvDirN++;
                __result = cached;
                return false;
            }
            _sfvProbeSw = System.Diagnostics.Stopwatch.StartNew();
            return true;
        }

        private static Exception DirProbeExit(string __0, ref bool __result,
            Exception __exception)
        {
            if (!_inScanForVars) return __exception;
            if (string.IsNullOrEmpty(__0) || __0[__0.Length - 1] != ':')
                return __exception;
            if (_sfvDirMemo == null)
                _sfvDirMemo = new Dictionary<string, bool>(StringComparer.Ordinal);
            _sfvDirMemo[__0] = __result;
            _sfvDirN++;
            if (_sfvProbeSw != null)
            {
                _sfvDirTicks += _sfvProbeSw.ElapsedTicks;
                _sfvProbeSw = null;
            }
            return __exception;
        }

        private static bool DirProbePrefix2(string __0, bool __1, ref bool __result)
        {
            return DirProbePrefix(__0, ref __result);
        }

        private static Exception DirProbeExit2(string __0, bool __1,
            ref bool __result, Exception __exception)
        {
            return DirProbeExit(__0, ref __result, __exception);
        }


        // ---------- probe + memo: clothing container sync ----------

        private static void ClearFafMemo()
        {
            if (_fafMemo.Count > 0) _fafMemo.Clear();
        }

        // The male half of the container sync only has work when something
        // under its own root moved; the diff verdict says exactly that. Every
        // doubt (no verdict, a selector outside the pass snapshot, a scene
        // load, an unclassifiable local clothing change, an empty male
        // container that still needs its first fill) keeps the native call.
        private static bool MaleCatalogSkippable(object selector)
        {
            if (!Enabled || !_sceneRebuildDecided) return false;
            if (SceneLoadAccelerator.SceneLoadActive) return false;
            if (_needClothingMale) return false;
            if (_localClothingHairDirty) return false;
            if (!SelectorCoveredByVerdict(selector)) return false;
            if (ContainerChildren(selector, _dcsMaleClothingC) <= 0) return false;
            return CalledFromGatedPath();
        }

        // Only the clothing root of the sync may be skipped on this verdict:
        // RefreshDynamicHair hands SyncCustomItems "Custom/Hair/Male/" through
        // the same method, and a male clothing verdict says nothing about it.
        private static bool IsMaleClothingRoot(string pattern)
        {
            if (string.IsNullOrEmpty(pattern)) return false;
            string p = pattern.Replace('/', '\\').ToLowerInvariant();
            return p.IndexOf("clothing\\male", StringComparison.Ordinal) >= 0;
        }

        // "Custom/Clothing/Female/" also contains "male" (case-insensitive),
        // so the tag has to key on the separator, not on the word.
        private static string SyncGenderTag(string pattern)
        {
            if (string.IsNullOrEmpty(pattern)) return "?";
            string p = pattern.Replace('/', '\\').ToLowerInvariant();
            if (p.IndexOf("\\male", StringComparison.Ordinal) >= 0) return "男";
            if (p.IndexOf("\\female", StringComparison.Ordinal) >= 0) return "女";
            return "?";
        }

        private static bool IsMaleClothingPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string p = "\\" + path.Replace('/', '\\').ToLowerInvariant();
            return p.IndexOf("\\clothing\\male\\",
                StringComparison.Ordinal) >= 0;
        }

        // VaM enables its own SyncCustomItems file/meta cache only while a
        // scene loads (SuperController LoadCo). Outside that window every
        // container sync re-lists the item tree and re-parses every item
        // meta, which is what the per-atom repeats in one callback pass pay
        // for. Bracketing the pass with the same toggle makes the repeats
        // read the cache; Disable clears the dictionaries again, so the state
        // the engine left behind is restored exactly.
        private static void InitClothesPrefix()
        {
            if (_sceneRebuildDecided)
                _swInitClothes = System.Diagnostics.Stopwatch.StartNew();
        }

        private static Exception InitClothesFinalizer(Exception __exception)
        {
            if (_swInitClothes != null)
            {
                _msInitClothes += _swInitClothes.Elapsed.TotalMilliseconds;
                _nInitClothes++;
                _swInitClothes = null;
            }
            return __exception;
        }

        private static bool DynUiResyncPrefix(object __instance)
        {
            _resyncInst = null;
            if (!_sceneRebuildDecided) return true;
            if (MaleCatalogUiSkippable(__instance))
            {
                // Nothing under Custom/Clothing/Male moved, so this
                // catalogue can only be rebuilt from the entries it already
                // holds: drop it for good instead of replaying it when the
                // panel opens.
                _skipResyncMaleUi++;
                return false;
            }
            if (ShouldSkipResync(__instance))
            {
                _skipResyncUi++;
                MarkResyncPending(__instance);
                return false;
            }
            _resyncInst = __instance;
            _swResync = System.Diagnostics.Stopwatch.StartNew();
            return true;
        }

        private static Exception DynUiResyncFinalizer(Exception __exception)
        {
            if (_swResync != null)
            {
                double ms = _swResync.Elapsed.TotalMilliseconds;
                _msResync += ms;
                _nResync++;
                _swResync = null;
                LogResyncDetail(ms);
            }
            _resyncInst = null;
            return __exception;
        }

        // The female and male clothing catalogues are the same class with the
        // same object-name pattern, so an instance is matched against the
        // selector's own field. Every doubt keeps the native rebuild.
        private static bool MaleCatalogUiSkippable(object inst)
        {
            if (!Enabled || !_sceneRebuildDecided) return false;
            if (SceneLoadAccelerator.SceneLoadActive) return false;
            if (_needClothingMale || _localClothingHairDirty) return false;
            if (!_needClothing) return false;
            if (_dynSelCharSelector == null || _dcsClothingMaleUi == null)
                return false;
            try
            {
                object sel = _dynSelCharSelector.GetValue(inst);
                if (sel == null) return false;
                if (!ReferenceEquals(_dcsClothingMaleUi.GetValue(sel), inst))
                    return false;
                if (!SelectorCoveredByVerdict(sel)) return false;
                if (ContainerChildren(sel, _dcsMaleClothingC) <= 0)
                    return false;
                return CalledFromGatedPath();
            }
            catch { return false; }
        }

        private static bool ShouldSkipResync(object inst)
        {
            if (!Enabled || !DeferHiddenResync) return false;
            return IsPanelHidden(inst as UnityEngine.Component);
        }

        // Hidden = the panel object (or an ancestor) is inactive, or the
        // canvas it lives on is switched off. Everything else counts as
        // visible, so anything uncertain still gets its native rebuild.
        internal static bool IsPanelHidden(UnityEngine.Component c)
        {
            if (c == null) return false;
            UnityEngine.GameObject go = c.gameObject;
            if (go == null) return false;
            if (!go.activeInHierarchy) return true;
            try
            {
                UnityEngine.Canvas cv = c.GetComponentInParent<UnityEngine.Canvas>();
                if (cv != null && !cv.enabled) return true;
            }
            catch { }
            return false;
        }

        private static void MarkResyncPending(object inst)
        {
            UnityEngine.Component c = inst as UnityEngine.Component;
            if (c == null || _pendingResync.Contains(c)) return;
            _pendingResync.Add(c);
        }

        // Used by SceneResyncCoalesce: a selector whose rebuild was skipped
        // inside a scene-load window is rebuilt on the frame its panel becomes
        // visible, which is what PumpPendingResync already does for hidden
        // panels whose rescan was deferred.
        internal static void DeferResyncToOpen(UnityEngine.Component c)
        {
            MarkResyncPending(c);
        }

        // Called every frame from Tick(); empty list = one comparison.
        private static void PumpPendingResync()
        {
            if (_pendingResync.Count == 0) return;
            for (int i = _pendingResync.Count - 1; i >= 0; i--)
            {
                UnityEngine.Component c = _pendingResync[i];
                if (c == null) { _pendingResync.RemoveAt(i); continue; }
                UnityEngine.GameObject go = c.gameObject;
                if (go == null) { _pendingResync.RemoveAt(i); continue; }
                if (IsPanelHidden(c)) continue;
                _pendingResync.RemoveAt(i);
                try
                {
                    long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    if (_dynUiResync != null) _dynUiResync.Invoke(c, null);
                    Log("服装UI延迟刷新：" + c.GetType().Name + "/" +
                        (go == null ? "?" : go.name) + " 补一次 Resync " +
                        Ms(System.Diagnostics.Stopwatch.GetTimestamp() - t0) +
                        "ms");
                }
                catch (Exception e)
                {
                    Log("服装UI延迟刷新失败：" + e.Message);
                }
            }
        }

        private static void LogResyncDetail(double ms)
        {
            try
            {
                UnityEngine.Component c = _resyncInst as UnityEngine.Component;
                if (c == null)
                {
                    Log("服装UI刷新#" + _nResync + " <null> " +
                        ms.ToString("F0") + "ms");
                    return;
                }
                UnityEngine.GameObject go = c.gameObject;
                bool visible = !IsPanelHidden(c);
                string caller = ResyncCaller();
                Log("服装UI刷新#" + _nResync + " " + c.GetType().Name + "/" +
                    (go == null ? "?" : go.name) + " 可见=" +
                    (visible ? "是" : "否") + " 调用方=" + caller + " " +
                    ms.ToString("F0") + "ms");
            }
            catch { }
        }

        private static string ResyncCaller()
        {
            try
            {
                var st = new System.Diagnostics.StackTrace(1, false);
                for (int i = 0; i < st.FrameCount && i < 10; i++)
                {
                    System.Diagnostics.StackFrame fr = st.GetFrame(i);
                    if (fr == null) continue;
                    MethodBase mi = fr.GetMethod();
                    if (mi == null || mi.DeclaringType == null) continue;
                    if (mi.DeclaringType.Namespace == "Quest3TriggerUI")
                        continue;
                    if (mi.Name == "Resync" || mi.Name == "ResyncUI" ||
                        mi.Name == "ResyncItems") continue;
                    return mi.DeclaringType.Name + "." + mi.Name;
                }
            }
            catch { }
            return "-";
        }

        private static void SyncItemsCacheBegin()
        {
            if (!Enabled || _syncItemsCacheOn || _syncItemsCacheEnable == null)
                return;
            try
            {
                _syncItemsCacheEnable.Invoke(null, null);
                _syncItemsCacheOn = true;
            }
            catch { }
        }

        private static void SyncItemsCacheEnd()
        {
            if (!_syncItemsCacheOn) return;
            _syncItemsCacheOn = false;
            try
            {
                if (_syncItemsCacheDisable != null)
                    _syncItemsCacheDisable.Invoke(null, null);
            }
            catch { }
        }

        private static bool ScsPrefix(object __instance, UnityEngine.Transform __4,
            string __2)
        {
            _fafCallN = _fafCallSum = _fafServeCall = 0;
            _fafCallTicks = 0;
            _sortCallN = _sortCallSum = 0;
            _sortCallTicks = 0;
            _fafServed = false;
            if (!Enabled) return true;
            if (IsMaleClothingRoot(__2) && MaleCatalogSkippable(__instance))
            {
                // The diff proved nothing under Custom/Clothing/Male moved, so
                // the container can only be rebuilt from the entries it holds.
                _skipClothesMale++;
                if (!_maleSkipLogged)
                {
                    _maleSkipLogged = true;
                    Log("男性服装容器：本趟差异不含男装，跳过同步");
                }
                return false;
            }
            _scsActive = true;
            _scsStart = System.Diagnostics.Stopwatch.GetTimestamp();
            _scsIn = __4 != null ? __4.childCount : -1;
            _scsWhat = SyncGenderTag(__2);
            return true;
        }

        private static Exception ScsFinalizer(UnityEngine.Transform __4,
            Exception __exception)
        {
            if (!_scsActive) return __exception;
            _scsActive = false;
            long total = System.Diagnostics.Stopwatch.GetTimestamp() - _scsStart;
            Log("服装容器同步[" + _scsWhat + "] 入场 " + _scsIn + " 条 → 出场 " +
                (__4 != null ? __4.childCount : -1) + " 条 / 列表 " + _fafCallN +
                " 次 " + Ms(_fafCallTicks) + "ms 共 " + _fafCallSum + " 条（记忆 " +
                _fafServeCall + "）/ 排序 " + _sortCallN + " 次 " +
                Ms(_sortCallTicks) + "ms 共 " + _sortCallSum + " 条 / 本次 " +
                Ms(total) + "ms");
            return __exception;
        }

        private static bool ItemListMemoable(string __0)
        {
            if (string.IsNullOrEmpty(__0)) return false;
            return __0.IndexOf("Clothing", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   __0.IndexOf("Hair", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool FafPrefix(string __0, string __1, object __2, bool __3)
        {
            if (!_scsActive || !Enabled) return true;
            _fafCallStart = System.Diagnostics.Stopwatch.GetTimestamp();
            if (__2 == null || !ItemListMemoable(__0)) return true;
            var list = __2 as System.Collections.IList;
            if (list == null || list.Count != 0) return true;
            string key = __0 + "|" + (__1 == null ? "" : __1) + "|" +
                (__3 ? "1" : "0");
            object[] cached;
            if (!_fafMemo.TryGetValue(key, out cached) || cached == null)
                return true;
            for (int i = 0; i < cached.Length; i++) list.Add(cached[i]);
            _fafCallN++;
            _fafCallSum += cached.Length;
            _fafServeCall++;
            _fafServed = true;
            _fafCallTicks += System.Diagnostics.Stopwatch.GetTimestamp() - _fafCallStart;
            return false;
        }

        private static void FafFinalizer(string __0, string __1, object __2,
            bool __3, Exception __exception)
        {
            if (!_scsActive || !Enabled || _fafServed) return;
            _fafCallTicks += System.Diagnostics.Stopwatch.GetTimestamp() - _fafCallStart;
            var list = __2 as System.Collections.IList;
            if (list == null) return;
            _fafCallN++;
            _fafCallSum += list.Count;
            if (__exception != null || list.Count == 0 || !ItemListMemoable(__0))
                return;
            if (_fafMemo.Count >= 8) _fafMemo.Clear();
            object[] arr = new object[list.Count];
            list.CopyTo(arr, 0);
            _fafMemo[__0 + "|" + (__1 == null ? "" : __1) + "|" +
                (__3 ? "1" : "0")] = arr;
        }

        private static void SortPrefix(object __0)
        {
            if (_scsActive && Enabled)
                _sortCallStart = System.Diagnostics.Stopwatch.GetTimestamp();
        }

        private static Exception SortFinalizer(object __0, Exception __exception)
        {
            if (_scsActive && Enabled)
            {
                _sortCallTicks += System.Diagnostics.Stopwatch.GetTimestamp() - _sortCallStart;
                var c = __0 as System.Collections.ICollection;
                _sortCallN++;
                _sortCallSum += c != null ? c.Count : 0;
            }
            return __exception;
        }

        // ---------- patch: FileManagerSecure listing memo ----------

        private static bool FmsListPrefix(string __0, string __1, char kind,
            ref string[] __result)
        {
            _fmsPend = false;
            if (!Enabled || !_inScanForVars) return true;
            if (string.IsNullOrEmpty(__0)) return true;
            if (!_fmsLoaded) LoadFmsListings();
            try
            {
                long sw = System.Diagnostics.Stopwatch.GetTimestamp();
                long mtime = Directory.GetLastWriteTimeUtc(__0).Ticks;
                _fmsStatTicks += System.Diagnostics.Stopwatch.GetTimestamp() - sw;
                string key = kind + "|" + __0 + "|" +
                    (__1 == null ? "" : __1);
                FmsListing hit;
                if (_fmsListings.TryGetValue(key, out hit) &&
                    hit.Mtime == mtime)
                {
                    _fmsServeN++;
                    __result = hit.Values == null
                        ? null : (string[])hit.Values.Clone();
                    return false;
                }
                if (hit != null) _fmsListings.Remove(key);
                if (!Directory.Exists(__0)) return true;
                _fmsNativeN++;
                _fmsPendKey = key;
                _fmsPendMtime = mtime;
                _fmsPend = true;
            }
            catch { }
            return true;
        }

        private static void FmsListPostfix(string __0, string __1, char kind,
            ref string[] __result)
        {
            if (!_fmsPend) return;
            string key = _fmsPendKey;
            long mtime = _fmsPendMtime;
            _fmsPend = false;
            if (key == null) return;
            try
            {
                if (_fmsListings.Count >= FmsMaxListings)
                    _fmsListings.Clear();
                FmsListing e = new FmsListing();
                e.Values = __result == null ? null : (string[])__result.Clone();
                e.Mtime = mtime;
                _fmsListings[key] = e;
                _fmsDirty = true;
            }
            catch { }
        }

        private static bool FmsFilesPrefix(string __0, string __1,
            ref string[] __result)
        {
            return FmsListPrefix(__0, __1, 'f', ref __result);
        }

        private static void FmsFilesPostfix(string __0, string __1,
            ref string[] __result)
        {
            FmsListPostfix(__0, __1, 'f', ref __result);
        }

        private static bool FmsDirsPrefix(string __0, string __1,
            ref string[] __result)
        {
            return FmsListPrefix(__0, __1, 'd', ref __result);
        }

        private static void FmsDirsPostfix(string __0, string __1,
            ref string[] __result)
        {
            FmsListPostfix(__0, __1, 'd', ref __result);
        }

        private static void LoadFmsListings()
        {
            if (_fmsLoaded) return;
            _fmsLoaded = true;
            try
            {
                if (!File.Exists(FmsCachePath)) return;
                string[] lines = File.ReadAllLines(FmsCachePath);
                for (int i = 0; i < lines.Length; i++)
                {
                    if (_fmsListings.Count >= FmsMaxListings) break;
                    string[] f = lines[i].Split('\t');
                    if (f.Length != 5 || f[0].Length != 1) continue;
                    long mtime;
                    if (!long.TryParse(f[3], out mtime)) continue;
                    string key = f[0] + "|" + f[1] + "|" + f[2];
                    FmsListing e = new FmsListing();
                    e.Mtime = mtime;
                    e.Values = f[4].Length == 0
                        ? new string[0] : f[4].Split('\u0001');
                    _fmsListings[key] = e;
                }
                Log("目录记忆载入 " + _fmsListings.Count + " 条");
            }
            catch (Exception e) { Log("目录记忆载入失败：" + e.Message); }
        }

        private static void SaveFmsListings()
        {
            if (!_fmsDirty) return;
            _fmsDirty = false;
            try
            {
                var sb = new StringBuilder();
                foreach (var kv in _fmsListings)
                {
                    FmsListing v = kv.Value;
                    int a = kv.Key.IndexOf('|');
                    int b = kv.Key.IndexOf('|', a + 1);
                    if (a != 1 || b < 0) continue;
                    sb.Append(kv.Key[0]).Append('\t');
                    sb.Append(kv.Key.Substring(2, b - 2)).Append('\t');
                    sb.Append(kv.Key.Substring(b + 1)).Append('\t');
                    sb.Append(v.Mtime).Append('\t');
                    if (v.Values != null)
                        for (int i = 0; i < v.Values.Length; i++)
                        {
                            if (i > 0) sb.Append('\u0001');
                            sb.Append(v.Values[i]);
                        }
                    sb.Append('\n');
                }
                File.WriteAllText(FmsCachePath, sb.ToString(),
                    new UTF8Encoding(false));
            }
            catch (Exception e) { Log("目录记忆写入失败：" + e.Message); }
        }        // ---------- patch: RescanPackages ----------        // The main "重新扫描本地及VAR" button (BrowserFiltersUI.RefreshContents)
        // calls this rather than RescanAllVARResources. Skipping it when the
        // library is unchanged removes SuperController.RescanPackages — the
        // dominant cost — while callers still run PostRescanRefresh themselves.
        // Ruleset-bearing callers (forceApplyRuleset / VAR management toggles)
        // always run the original.

        private static bool VarPkgsPrefix(bool __0)
        {
            // RescanPackages is a shared workhorse — ResetVARCache,
            // ApplyCurrentVARDisablingProfile, SetMorphPack, resource
            // deletion and hub-download paths all call it with semantics we
            // must not disturb. Only the "重新扫描本地及VAR" button
            // (RefreshContents coroutine) gets the fingerprint skip.
            if (!Enabled || !CalledFromGatedPath()) return true;
            _varSkippedPass = false;
            ClearFafMemo();
            _chRebuildRan = false;
            _sceneRebuildDecided = false;
            _sceneRebuildNeeded = false;
            _sceneRebuildWhy = null;            ResetPassAccounting();

            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                _truncated = false;
                // Reuse the fingerprint already computed by an enclosing
                // RescanAllVARResources instead of enumerating twice.
                Dictionary<string, string> cur = _inVarAll > 0 && _varPending != null
                    ? _varPending : VarFingerprint();
                if (_truncated)
                {
                    if (_inVarAll == 0) { _varPending = null; _varDirty = null; }
                    Log("VAR 指纹超限（>" + MaxEntries + " 条），回退原生全量");
                    return true;
                }
                int pop = -1;
                try { pop = CollectionCount(_availableVars.GetValue(null, null)); }
                catch { }
                bool skip = _varCache != null && pop > 0 && Equal(cur, _varCache);
                string why = skip ? "无变化" :
                    _varCache == null ? "无缓存" :
                    pop <= 0 ? "VAR清单为空(" + pop + ")" : "有变化";
                Log("VAR 指纹：" + cur.Count + " 条目，耗时 " +
                    sw.ElapsedMilliseconds + "ms，" + why + "；" +
                    LinkCacheStat());
                if (skip)
                {
                    _varSkippedPass = true;
                    if (_inVarAll == 0) { _varPending = null; _varDirty = null; }
                    return false;
                }
                _varPending = cur;
                _varDirty = DirtyVarPaths(cur, _varCache);
                ClassifyDirtyContent(_varDirty);
                Log("VAR 重扫：" + _varDirty.Count + " 个包新增/变更");
                if (_varCache != null)
                {
                    var diff = new List<string>();
                    foreach (var kv in cur)
                    {
                        string old;
                        if (!_varCache.TryGetValue(kv.Key, out old) ||
                            old != kv.Value)
                        {
                            diff.Add(kv.Key);
                            if (diff.Count >= 8) break;
                        }
                    }
                    foreach (var kv in _varCache)
                    {
                        if (!cur.ContainsKey(kv.Key))
                        {
                            diff.Add("-" + kv.Key);
                            if (diff.Count >= 8) break;
                        }
                    }
                    if (diff.Count > 0)
                        Log("VAR 指纹差异键：" + string.Join("、",
                            diff.ToArray()));
                }
                // Commit the fingerprint BEFORE the native rescan runs. A
                // hard crash (0x80000003) inside it never reaches the
                // finalizer — the migrated cache would be lost and every
                // restart would re-dirty the whole library, re-running the
                // crash. On a managed failure the finalizer invalidates the
                // cache so the next scan repairs a half-built manifest.
                if (_inVarAll == 0)
                {
                    _varCache = _varPending;
                    SaveCache();
                }
                return true;
            }
            catch (Exception e)
            {
                if (_inVarAll == 0) { _varPending = null; _varDirty = null; }
                Log("VAR 指纹异常，回退原生重扫：" + e.Message);
                return true;
            }
        }

        private static Exception VarPkgsFinalizer(Exception __exception)
        {
            // Inside RescanAllVARResources the outer finalizer owns the state;
            // standalone calls clear here. The cache was already committed in
            // the prefix; on a managed exception invalidate it so a possibly
            // half-built manifest gets a repair pass next scan.
            if (_inVarAll == 0)
            {
                if (__exception != null && _varPending != null)
                {
                    _varCache = null;
                    SaveCache();
                }
                _varPending = null; _varDirty = null;
                LogPassSummary();
            }
            return __exception;
        }

        private static bool VarMgmtActive()
        {
            try
            {
                return _varMgmtProp != null &&
                    (bool)_varMgmtProp.GetValue(null, null);
            }
            catch { return true; }   // unknown → conservative: never skip
        }

        // ---------- patch: refresh tail of RefreshContents ----------

        // RefreshContents ends with RefreshPersonClothing() (per-atom
        // RefreshDynamicItems + RefreshPackageMorphs, ~18s on a 3-atom
        // scene) — the same two calls the clothing-hair callback pass just
        // made. Skip it when the diff carries no clothing/hair/morph
        // content at all, or when this pass already rebuilt the lists.
        private static bool PersonClothingPrefix()
        {
            if (_refreshSkipped && CalledFromRefreshContents()) return false;
            if (SkipSceneRebuildForThisPass())
            {
                Log("人物服装列表：本趟差异不含服装/头发/形变，跳过重建（" +
                    _sceneRebuildWhy + "）");
                return false;
            }
            if (_chRebuildRan && CalledFromGatedPath() &&
                !_localClothingHairDirty)
            {
                Log("人物服装列表：本趟已重建，跳过重复重建");
                return false;
            }
            return true;
        }

        private static bool ResetCountsPrefix()
        {
            if (_refreshSkipped && CalledFromRefreshContents()) return false;
            if (SkipSceneRebuildForThisPass()) return false;
            return true;
        }

        private static void VpmeProbeEnter()
        {
            if (_inScanForVars) _sfvVpmeSw = System.Diagnostics.Stopwatch.StartNew();
        }

        private static Exception VpmeProbeExit(Exception __exception)
        {
            if (_inScanForVars && _sfvVpmeSw != null)
            {
                _sfvVpmeTicks += _sfvVpmeSw.ElapsedTicks;
                _sfvVpmeN++;
                _sfvVpmeSw = null;
            }
            return __exception;
        }

        private static long Ms(long ticks)
        {
            return ticks * 1000 / System.Diagnostics.Stopwatch.Frequency;
        }

        // Only skippable inside a fingerprinted pass that our own incremental
        // registration handled (small dirty set); every other caller — scene
        // load, VAR enable/disable, the manual memory button — stays native.
        private static bool MemOptimizePrefix()
        {
            if (!Enabled || _varDirty == null || _varDirty.Count > 64)
                return true;
            Log("内存优化：增量重扫跳过 TriggerOptimize（差异 " +
                _varDirty.Count + " 个包）");
            return false;
        }

        private static bool PostRescanPrefix()
        {
            _inPostRescan = true;
            _postSw = CalledFromRefreshContents()
                ? System.Diagnostics.Stopwatch.StartNew() : null;
            return true;
        }

        private static Exception PostRescanFinalizer(Exception __exception)
        {
            _inPostRescan = false;
            if (_postSw != null)
            {
                Log("PostRescanRefresh 耗时 " + _postSw.ElapsedMilliseconds + "ms");
                _postSw = null;
            }
            return __exception;
        }

        // True only when the caller is BrowserFiltersUI.RefreshContents'
        // iterator — i.e. the "重新扫描本地及VAR" button. A few frames of
        // stack walking costs milliseconds; the scan it gates costs tens of
        // seconds.
        private static bool CalledFromRefreshContents()
        {
            try
            {
                var frames = new System.Diagnostics.StackTrace(1, false).GetFrames();
                if (frames == null) return false;
                foreach (var f in frames)
                {
                    var m = f.GetMethod();
                    var dt = m == null ? null : m.DeclaringType;
                    string n = dt == null ? null : dt.FullName;
                    if (n != null && n.Contains("RefreshContents"))
                        return true;
                }
            }
            catch { }
            return false;
        }

        // A scene load re-enters the package and local-resource scan path
        // through SuperController. The fingerprint gate is exactly as valid
        // there: if nothing in the library moved, the rebuild is pure
        // overhead, and a library that did change still runs the native scan.
        private static bool CalledFromSceneLoad()
        {
            if (!SceneLoadAccelerator.SceneLoadActive) return false;
            try
            {
                var frames = new System.Diagnostics.StackTrace(1, false).GetFrames();
                if (frames == null) return false;
                foreach (var f in frames)
                {
                    var m = f.GetMethod();
                    var dt = m == null ? null : m.DeclaringType;
                    if (dt == null) continue;
                    string n = dt.FullName;
                    if (n == "SuperController" ||
                        n.StartsWith("MVR.FileManagement"))
                        return true;
                }
            }
            catch { }
            return false;
        }

        // Both gated callers mean the same thing: the library was fingerprinted
        // and only an unchanged library may skip the rebuild.
        private static bool CalledFromGatedPath()
        {
            return CalledFromRefreshContents() || CalledFromSceneLoad();
        }

        // ---------- patch: RescanVARResources ----------

        private static bool VarResPrefix(object __0, int __1)
        {
            HashSet<string> dirty = _varDirty;
            if (dirty == null) return true;   // standalone call — always run
            try
            {
                object vpme = _getVpme.Invoke(__0, new object[] { __1 });
                if (vpme == null) return true;
                string path = _varPathProp.GetValue(vpme, null) as string;
                if (path == null) return true;
                return dirty.Contains(Norm(path));
            }
            catch { return true; }
        }

        // ---------- patch: PC.ProcessMetaFileDependencies ----------
        // Runs once per package at the tail of every non-skipped rescan and
        // re-parses the meta cache — on this library that is ~10k JSON loads
        // even when a single var changed. During a fingerprinted dirty scan
        // only packages whose path is in the dirty set need reprocessing;
        // unchanged vpmes keep the dep data already stored on them.

        private static bool MetaDepsPrefix(object __0)
        {
            HashSet<string> dirty = _varDirty;
            if (dirty == null) return true;   // not a fingerprinted scan
            try
            {
                if (__0 == null) return true;
                string path = _varPathProp.GetValue(__0, null) as string;
                if (path == null) return true;
                return dirty.Contains(Norm(path));
            }
            catch { return true; }
        }

        // ---------- patch: LocalResourcesInit ----------

        private static bool LocalPrefix()
        {
            // Callers: BrowserAssist.Init (startup — must populate the
            // manifest) and the RefreshContents coroutine (the button).
            if (!Enabled || !CalledFromGatedPath()) return true;
            _refreshSkipped = false;
            _localClothingHairDirty = false;
            _localClassClothing = _localClassHair = _localClassMorphs = false;
            _localClassUnknown = false;
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                _truncated = false;
                var fileMap = new Dictionary<string, long>(
                    StringComparer.OrdinalIgnoreCase);
                long[] hashes, counts;
                LocalFingerprint(out hashes, out counts, fileMap);
                if (_truncated)
                {
                    _localPendingSet = false;
                    Log("本地指纹超限（>" + MaxEntries + " 条），回退原生全量");
                    return true;
                }
                int pop = -1;
                try { pop = CollectionCount(_localRvges.GetValue(null)); }
                catch { }
                bool same = EqualLongs(hashes, _localHashCache) &&
                    EqualLongs(counts, _localCountCache);
                bool skip = _localCacheSet && pop > 0 && same;
                long total = 0;
                foreach (long c in counts) total += c;
                if (skip)
                {
                    Log("本地指纹：" + total + " 文件，耗时 " +
                        sw.ElapsedMilliseconds + "ms，无变化，跳过全量重扫");
                    _localPendingSet = false;
                    // Neither half moved, so the VAR side never classified
                    // anything and every class gate is still inert: the
                    // trailing person rebuild would run natively (~16s on
                    // this library) for a pass that has nothing to apply.
                    // Stamp the verdict an empty diff would have produced.
                    // A scene load is the one case where new atoms can
                    // appear mid-pass, so it keeps the native call.
                    if (!_sceneRebuildDecided &&
                        !SceneLoadAccelerator.SceneLoadActive)
                    {
                        _needClothing = _needHair = _needMorphs = false;
                        _sceneRebuildNeeded = false;
                        _sceneRebuildWhy = "VAR/本地均无变化";
                        _sceneRebuildDecided = true;
                        SnapshotPassSelectors();
                    }
                    // Both halves skipped → the coroutine's trailing
                    // person-clothing rebuild and preset recount are pure
                    // overhead; gated prefixes below elide them. The
                    // coroutine invokes PostRescanRefresh itself — calling
                    // it here too would refresh the browser list twice.
                    _refreshSkipped = _varSkippedPass;
                    return false;
                }
                if (_localCacheSet && same)
                    Log("本地指纹未变但清单为空（localRVGEs=" + pop + ")，放行原生扫描");
                // Report which roots moved so a perpetually-dirty root
                // (autosave, plugin data) is visible instead of silent.
                if (_localCacheSet && !same)
                {
                    var roots = LocalRoots();
                    var diff = new List<string>();
                    for (int i = 0; i < hashes.Length && i < _localHashCache.Length; i++)
                        if (hashes[i] != _localHashCache[i] ||
                            counts[i] != _localCountCache[i])
                            diff.Add(i < roots.Count ? roots[i] : "#" + i);
                    if (hashes.Length != _localHashCache.Length)
                        diff.Add("根目录数量变化");
                    Log("本地指纹变化根：" + string.Join("、", diff.ToArray()));
                }
                Log("本地指纹：" + total + " 文件，耗时 " +
                    sw.ElapsedMilliseconds + "ms，有变化");
                _localHashPending = hashes; _localCountPending = counts;
                _localPendingSet = true;
                // Same early-commit rationale as the VAR side: survive a
                // hard crash inside the native rescan. Grab the baseline
                // for the file diff BEFORE the commit overwrites it.
                var baseline = _localFileCache;
                _localHashCache = _localHashPending;
                _localCountCache = _localCountPending;
                _localFilePending = fileMap;
                _localFileCache = _localFilePending;
                _localCacheSet = true;
                SaveCache();
                // A small file-level diff doesn't need the native
                // clear-everything-and-rescan (~20k re-registrations):
                // register/purge just the entries that moved through BA's
                // own per-file APIs. Baseline absent (old cache format),
                // empty manifest, oversized diff or any unresolved member
                // all fall through to the native rebuild.
                if (pop > 0 && TryIncrementalLocalUpdate(fileMap, baseline))
                {
                    // The VAR half of this pass came out clean, so it
                    // cleared the verdict and left the class gates inert:
                    // every class would rebuild natively (~30s on a 3
                    // person scene) even when a single local class moved.
                    // Stamp the verdict the local diff just produced.
                    if (!_sceneRebuildDecided)
                    {
                        _needClothing = _needHair = _needMorphs = false;
                        _sceneRebuildNeeded = _localClothingHairDirty;
                        _sceneRebuildWhy = "本地增量（服装" +
                            (_localClassClothing ? "√" : "×") + " 头发" +
                            (_localClassHair ? "√" : "×") + " 形变" +
                            (_localClassMorphs ? "√" : "×") + "）";
                        _sceneRebuildDecided = true;
                        SnapshotPassSelectors();
                    }
                    return false;
                }
                _localClothingHairDirty = true;
                _localClassUnknown = true;
                return true;
            }
            catch (Exception e)
            {
                _localPendingSet = false;
                Log("本地指纹异常，回退原生重扫：" + e.Message);
                return true;
            }
        }

        private static Exception LocalFinalizer(Exception __exception)
        {
            if (__exception != null && _localPendingSet)
            {
                _localCacheSet = false;
                _localFileCache = null;   // manifest state unknown — next
                SaveCache();              // scan must rebuild natively
            }
            _localPendingSet = false;
            return __exception;
        }

        private static bool EqualLongs(long[] a, long[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        // ---------- incremental local-resource update ----------

        // BA's LocalResourcesInit clears every local RVGE and re-registers
        // all ~20k files. When only a handful moved we do the equivalent
        // work per file through BA's own public statics:
        // GetResourceType/GetPresetAtomType classify, PurgeLocalRVGE
        // removes, RegisterLocalResourceOfType inserts. The fingerprint
        // map keys are Norm'd absolute paths; BA keys everything by
        // VaM-relative "Custom\Atom\..." form.
        private static bool TryIncrementalLocalUpdate(
            Dictionary<string, long> cur, Dictionary<string, long> baseline)
        {
            if (baseline == null) return false;   // old cache has no map
            if (_regLocal == null || _purgeLocal == null ||
                _getResType == null || _getPresetType == null ||
                _rvgeDictField == null)
                return false;
            System.Collections.IDictionary rvgeDict;
            try
            {
                rvgeDict = _rvgeDictField.GetValue(null)
                    as System.Collections.IDictionary;
            }
            catch { return false; }
            if (rvgeDict == null) return false;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var removed = new List<string>();
            var upsert = new List<string>();
            foreach (var kv in baseline)
                if (!cur.ContainsKey(kv.Key)) removed.Add(kv.Key);
            foreach (var kv in cur)
            {
                long old;
                if (!baseline.TryGetValue(kv.Key, out old) || old != kv.Value)
                    upsert.Add(kv.Key);
            }
            int total = removed.Count + upsert.Count;
            if (total == 0) return true;   // root list shifted, files didn't
            if (total > MaxIncrementalLocal)
            {
                Log("本地差异 " + total + " 文件，超过增量上限，回退原生全量");
                return false;
            }

            // User data (favourites/hidden/alias/notes) lives in
            // LocalResourcesUserData\*.userData keyed by
            // resourceFullFileName. The native rebuild re-applies it via
            // ReloadLocalDataFiles; we re-apply per changed file.
            var udata = LoadLocalUserDataIndex();
            bool touchedCH = false;
            bool touchedC = false, touchedH = false, touchedM = false;
            int reg = 0, purged = 0, skipped = 0;

            foreach (string abs in removed)
            {
                string rel = CanonRel(abs);
                if (IsSceneContentPath(rel))
                {
                    touchedCH = true;
                    bool c0 = false, h0 = false, m0 = false;
                    ContentClassOf(rel, ref c0, ref h0, ref m0);
                    touchedC |= c0;
                    touchedH |= h0;
                    touchedM |= m0;
                }                try
                {
                    object rvge = rvgeDict.Contains(rel)
                        ? rvgeDict[rel] : null;
                    if (rvge != null)
                    {
                        _purgeLocal.Invoke(null, new[] { rvge });
                        purged++;
                    }
                    else skipped++;
                    // Queue the user file for re-save so the dead entry
                    // drops out — mirrors ReloadLocalDataFiles' tail.
                    KeyValuePair<string, JSONClass> ud;
                    if (udata != null && udata.TryGetValue(rel, out ud) &&
                        _regUserSaveReq != null)
                        try
                        {
                            _regUserSaveReq.Invoke(null,
                                new object[] { ud.Key });
                        }
                        catch { }
                }
                catch (Exception e)
                {
                    Log("本地增量移除失败 " + rel + "：" + e.Message +
                        "，回退原生全量");
                    return false;
                }
            }
            foreach (string abs in upsert)
            {
                string rel = CanonRel(abs);
                if (IsSceneContentPath(rel))
                {
                    touchedCH = true;
                    bool c0 = false, h0 = false, m0 = false;
                    ContentClassOf(rel, ref c0, ref h0, ref m0);
                    touchedC |= c0;
                    touchedH |= h0;
                    touchedM |= m0;
                }                try
                {
                    int type = (int)_getResType.Invoke(null,
                        new object[] { rel });
                    if (type == _rtUnknown) { skipped++; continue; }
                    string atomType = "";
                    if (type == _rtPresetAtom)
                    {
                        int cat = _getResCat == null ? _rcPreset
                            : (int)_getResCat.Invoke(null,
                                new object[] { type });
                        atomType = (string)_getPresetType.Invoke(null,
                            new object[] { cat, type, rel });
                        if (string.IsNullOrEmpty(atomType))
                        { skipped++; continue; }
                    }
                    if (IsClothingHairResType(type))
                    {
                        touchedCH = true;
                        if (type == _rtFemaleHair || type == _rtMaleHair ||
                            type == _rtHairItemPresets)
                            touchedH = true;
                        else
                            touchedC = true;
                    }                    // Folder-browser entry for new directories — ScanNow
                    // does the same before registering.
                    if (_getFolder != null)
                        try
                        {
                            _getFolder.Invoke(null, new object[]
                            {
                                Path.GetDirectoryName(rel), type, atomType
                            });
                        }
                        catch { }
                    object old = rvgeDict.Contains(rel)
                        ? rvgeDict[rel] : null;
                    if (old != null)
                        _purgeLocal.Invoke(null, new[] { old });
                    object rvge = _regLocal.Invoke(null, new object[]
                        { rel, type, atomType, DateTime.Now, false });
                    KeyValuePair<string, JSONClass> ud;
                    if (udata != null && udata.TryGetValue(rel, out ud))
                    {
                        MethodInfo lud = rvge == null ? null :
                            rvge.GetType().GetMethod("LoadLocalUserData",
                                BindingFlags.Instance | BindingFlags.Public);
                        if (lud == null || _regExistingUD == null)
                        {
                            Log("本地增量用户数据回绑不可用 " + rel +
                                "，回退原生全量");
                            return false;
                        }
                        _regExistingUD.Invoke(null,
                            new object[] { rvge, ud.Key });
                        lud.Invoke(rvge, new object[] { ud.Value });
                    }
                    reg++;
                }
                catch (Exception e)
                {
                    Log("本地增量注册失败 " + rel + "：" + e.Message +
                        "，回退原生全量");
                    return false;
                }
            }
            _localClothingHairDirty = touchedCH;
            _localClassClothing = touchedC;
            _localClassHair = touchedH;
            _localClassMorphs = touchedM;
            Log("本地增量：注册 " + reg + " 移除 " + purged + " 跳过 " +
                skipped + "（共 " + total + "），耗时 " +
                sw.ElapsedMilliseconds + "ms" +
                (touchedCH ? "，含服装/头发（服装" + (touchedC ? "√" : "×") +
                    " 头发" + (touchedH ? "√" : "×") + " 形变" +
                    (touchedM ? "√" : "×") + "）" : ""));
            return true;
        }

        private static bool IsClothingHairResType(int t)
        {
            return t == _rtFemaleClothing || t == _rtMaleClothing ||
                t == _rtFemaleHair || t == _rtMaleHair ||
                t == _rtClothingItemPresets || t == _rtHairItemPresets;
        }

        private static string _vamRootNorm;
        private static string CanonRel(string path)
        {
            // Fingerprint keys are Norm'd absolute paths; BA resource keys
            // are VaM-relative. Strip the game-root prefix when present.
            if (_vamRootNorm == null)
                try
                {
                    _vamRootNorm = Norm(Directory.GetCurrentDirectory());
                }
                catch { _vamRootNorm = ""; }
            string n = Norm(path);
            if (_vamRootNorm.Length > 0 &&
                n.StartsWith(_vamRootNorm + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
                return n.Substring(_vamRootNorm.Length + 1);
            return n;
        }

        // Index LocalResourcesUserData\*.userData → resource path →
        // (file, resource node). Files are few (one per user-data file,
        // not per resource); parsed fresh each incremental pass.
        private static Dictionary<string, KeyValuePair<string, JSONClass>>
            LoadLocalUserDataIndex()
        {
            var map = new Dictionary<string, KeyValuePair<string, JSONClass>>(
                StringComparer.OrdinalIgnoreCase);
            try
            {
                const string dir = "Saves\\PluginData\\JayJayWon\\" +
                    "BrowserAssist\\LocalResourcesUserData";
                if (!Directory.Exists(dir)) return map;
                foreach (string f in Directory.GetFiles(dir, "*.userData"))
                {
                    JSONClass jc;
                    try { jc = JSON.Parse(File.ReadAllText(f)).AsObject; }
                    catch { continue; }
                    if (jc == null) continue;
                    JSONArray arr = jc["resources"].AsArray;
                    if (arr == null) continue;
                    foreach (JSONNode n in arr)
                    {
                        JSONClass r = n.AsObject;
                        string p = r == null ? null
                            : r["resourceFullFileName"].Value;
                        if (string.IsNullOrEmpty(p)) continue;
                        string rel = CanonRel(p);
                        if (!map.ContainsKey(rel))
                            map[rel] =
                                new KeyValuePair<string, JSONClass>(f, r);
                    }
                }
            }
            catch { }
            return map;
        }

        // PostRescanRefresh re-runs RefreshClothingHairItemCallbacks right
        // after the RescanPackages tail already did (~17-19s each on this
        // library). The second pass is a pure duplicate only when the
        // first actually ran this scan AND no clothing/hair-typed local
        // file moved meanwhile — otherwise let it run (e.g. VAR-clean
        // scans never entered RescanPackages, and a scene change may have
        // added atoms that still need their callbacks).
        private static bool ClothingHairPrefix()
        {
            if (_inQuickScan)
            {
                Log("服装/头发回调：轻扫模式不触碰场景原子，跳过重建" +
                    (_localClothingHairDirty || _quickScanSawCH
                        ? "（检测到相关变更——「快速扫描▸服装/头发」可单独刷新）"
                        : ""));
                return false;
            }
            if (SkipSceneRebuildForThisPass())
            {
                Log("服装/头发回调：本趟差异不含服装/头发/形变，跳过重建（" +
                    _sceneRebuildWhy + "）");
                return false;
            }
            if (_chRebuildRan && CalledFromGatedPath() &&
                !_localClothingHairDirty)
            {
                Log("服装/头发回调：本趟已重建，跳过重复重建");
                return false;
            }
            if (_inPostRescan && CalledFromGatedPath() &&
                _chRebuildRan && !_localClothingHairDirty)
            {
                Log("服装/头发回调：本趟已重建且无相关本地变更，跳过二次重建");
                return false;
            }
            _chRebuildRan = true;
            _quickScanSawCH = false;   // a real rebuild satisfies the pending hint
            SyncItemsCacheBegin();
            return true;
        }

        private static Exception ClothingHairFinalizer(Exception __exception)
        {
            SyncItemsCacheEnd();
            return __exception;
        }

        // ---------- class gates: clothing / hair / morphs ----------

        // Maps one archive entry (or one VaM-relative local path) onto the
        // content class whose native primitive actually reads that path.
        // RefreshDynamicClothes only syncs its catalogs from the two
        // searchPath roots it hands to SyncCustomItems ("Custom/Clothing/
        // Female/" and "Custom/Clothing/Male/"), RefreshDynamicHair from
        // "Custom/Hair/Female|Male/", so a path outside those roots cannot
        // change an item catalog. Clothing / hair *presets* live under
        // Custom/Atom/Person/Clothing|Hair and are read by neither of them.
        // Morphs are the .vmi/.vmb definition files the banks import;
        // morph *presets* (.vap/.jpg) share that folder but are read by
        // no morph primitive.
        // The leading separator is prepended so a root that is the very first
        // path segment ("Custom/Clothing/...") still matches the boundary.
        private static void ContentClassOf(string path, ref bool clothing,
            ref bool hair, ref bool morphs)
        {
            if (string.IsNullOrEmpty(path)) return;
            string p = "\\" + path.Replace('/', '\\').ToLowerInvariant();
            if (p.EndsWith(".vmi") || p.EndsWith(".vmb")) morphs = true;
            if (p.IndexOf("\\custom\\clothing\\", StringComparison.Ordinal) >= 0)
                clothing = true;
            if (p.IndexOf("\\custom\\hair\\", StringComparison.Ordinal) >= 0)
                hair = true;
            // Morph *presets* (.vap / .jpg) also live under that folder,
            // but neither morph primitive reads them, so only the
            // definition files may flag the class: a saved preset used
            // to force a full morph-bank re-import for every person.
        }

        private static void SnapshotPassSelectors()
        {
            var set = new HashSet<int>();
            try
            {
                UnityEngine.Object[] all = UnityEngine.Object.FindObjectsOfType(
                    typeof(DAZCharacterSelector));
                for (int i = 0; i < all.Length; i++)
                    set.Add(all[i].GetInstanceID());
            }
            catch { }
            _passSelectors = set;
        }

        private static bool SelectorCoveredByVerdict(object selector)
        {
            var o = selector as UnityEngine.Object;
            if (o == null || _passSelectors == null) return false;
            return _passSelectors.Contains(o.GetInstanceID());
        }

        private static void ResetPassAccounting()
        {
            _msClothes = _msHair = _msMorph = 0;
            _nClothes = _nHair = _nMorph = 0;
            _skipClothes = _skipHair = _skipMorph = 0;
            _skipClothesMale = 0;
            _maleSkipLogged = false;
            _needClothingMale = false;
            _msInitClothes = _msResync = 0;
            _nInitClothes = _nResync = 0;
            _skipResyncUi = _skipResyncMaleUi = 0;
            _removedCount = 0;
            _swInitClothes = _swResync = null;
            _incMorphPkgs = 0;
            _morphPurgePkgs = _morphPurgeItems = _morphPurgeZeroed = 0;
            _swClothes = _swHair = _swMorph = null;
        }

        // The class verdict of the last fingerprinted diff stays valid until
        // the next pass begins: a Person atom's clothing / hair / morph
        // catalogs are only refilled by a rescan or a scene load, so a class
        // the diff proved clean has nothing to rebuild even when the call
        // arrives after the pass ended (RefreshDynamicItems defers to a
        // coroutine while the hub is open and replays it when the hub closes).
        // The verdict is bound to the selectors that existed when it was made,
        // so it can never skip the first sync of a person added later.
        // Every doubt — no verdict, a selector outside the snapshot, a scene
        // load in flight, a local file of that class, an unclassifiable local
        // diff — keeps the native call.
        private static bool ClassRebuildSkippable(char kind, object selector)
        {
            if (!Enabled || !_sceneRebuildDecided) return false;
            if (SceneLoadAccelerator.SceneLoadActive) return false;
            if (!SelectorCoveredByVerdict(selector)) return false;
            if (_localClassUnknown) return false;
            bool need = kind == 'c' ? _needClothing
                : kind == 'h' ? _needHair : _needMorphs;
            if (need) return false;
            bool localDirty = kind == 'c' ? _localClassClothing
                : kind == 'h' ? _localClassHair : _localClassMorphs;
            return !localDirty;
        }

        // A selector whose catalogs were never filled must sync natively —
        // that is how a fresh atom (scene load, "add person") gets its item
        // lists. An empty container is the cheap way to recognise one.
        private static bool SelectorCatalogReady(object selector, FieldInfo a,
            FieldInfo b)
        {
            return ContainerChildren(selector, a) > 0 ||
                   ContainerChildren(selector, b) > 0;
        }

        private static int ContainerChildren(object selector, FieldInfo f)
        {
            if (selector == null || f == null) return -1;
            try
            {
                Transform t = f.GetValue(selector) as Transform;
                return t == null ? -1 : t.childCount;
            }
            catch { return -1; }
        }

        // A removed package leaves a stale morph behind only in a bank that
        // still counts its uid as loaded (or preloaded). Asking the banks is
        // exact: a clothing package never lands there, and one that does
        // forces the native re-import. Every doubt answers "rebuild".
        private static bool RemovedLoadedInMorphBanks(List<string> removedPaths)
        {
            if (removedPaths == null || removedPaths.Count == 0) return false;
            if (_pkgPathToUid == null || _morphBankType == null ||
                _morphBankLoadedUids == null || _morphBankPreloadUids == null)
                return true;
            try
            {
                var uids = new HashSet<string>(StringComparer.Ordinal);
                string cwd = null;
                for (int i = 0; i < removedPaths.Count; i++)
                {
                    string rel = ToRelative(removedPaths[i], ref cwd);
                    string uid = _pkgPathToUid.Invoke(null,
                        new object[] { rel }) as string;
                    if (string.IsNullOrEmpty(uid)) return true;
                    uids.Add(uid);
                }
                UnityEngine.Object[] banks =
                    UnityEngine.Resources.FindObjectsOfTypeAll(_morphBankType);
                if (banks == null || banks.Length == 0) return false;
                for (int i = 0; i < banks.Length; i++)
                {
                    if (banks[i] == null) continue;
                    if (UidSetHits(_morphBankLoadedUids, banks[i], uids) ||
                        UidSetHits(_morphBankPreloadUids, banks[i], uids))
                        return true;
                }
                return false;
            }
            catch { return true; }
        }

        private static bool UidSetHits(FieldInfo f, object bank,
            HashSet<string> uids)
        {
            try
            {
                var set = f.GetValue(bank) as System.Collections.IEnumerable;
                if (set == null) return false;
                foreach (object o in set)
                    if (o != null && uids.Contains(o.ToString())) return true;
            }
            catch { }
            return false;
        }

        private static bool MorphBankInitialized(object bank)
        {
            if (_morphBankLoadedUids == null || _morphBankPreloadUids == null)
                return false;
            try
            {
                return _morphBankLoadedUids.GetValue(bank) != null &&
                       _morphBankPreloadUids.GetValue(bank) != null;
            }
            catch { return false; }
        }

        // uids of every package the current fingerprint reports as
        // added/changed. null when any dirty path cannot be resolved to a
        // uid — the set then proves nothing and the shortcut stays off.
        private static HashSet<string> DirtyPackageUids()
        {
            try
            {
                if (_varDirty == null || _pkgPathToUid == null) return null;
                var set = new HashSet<string>();
                string cwd = null;
                foreach (string path in _varDirty)
                {
                    string rel = ToRelative(path, ref cwd);
                    string uid = _pkgPathToUid.Invoke(null,
                        new object[] { rel }) as string;
                    if (string.IsNullOrEmpty(uid)) return null;
                    set.Add(uid);
                }
                return set;
            }
            catch { return null; }
        }

        // ---------- patch: RefreshDynamicClothes / RefreshDynamicHair ----------
        // RefreshDynamicItems is exactly these two calls in sequence, so the
        // clothing and hair catalogs can be refreshed independently instead of
        // paying for both on every rescan.

        private static bool DynamicClothesPrefix(object __instance)
        {
            if (ClassRebuildSkippable('c', __instance) && SelectorCatalogReady(__instance,
                    _dcsFemaleClothingC, _dcsMaleClothingC))
            {
                _skipClothes++;
                return false;
            }
            if (_sceneRebuildDecided)
                _swClothes = System.Diagnostics.Stopwatch.StartNew();
            return true;
        }

        private static Exception DynamicClothesFinalizer(Exception __exception)
        {
            if (_swClothes != null)
            {
                _msClothes += _swClothes.Elapsed.TotalMilliseconds;
                _nClothes++;
                _swClothes = null;
            }
            return __exception;
        }

        private static bool DynamicHairPrefix(object __instance)
        {
            if (ClassRebuildSkippable('h', __instance) && SelectorCatalogReady(__instance,
                    _dcsFemaleHairC, _dcsMaleHairC))
            {
                _skipHair++;
                return false;
            }
            if (_sceneRebuildDecided)
                _swHair = System.Diagnostics.Stopwatch.StartNew();
            return true;
        }

        private static Exception DynamicHairFinalizer(Exception __exception)
        {
            if (_swHair != null)
            {
                _msHair += _swHair.Elapsed.TotalMilliseconds;
                _nHair++;
                _swHair = null;
            }
            return __exception;
        }

        // ---------- patch: DAZMorphBank.RefreshPackageMorphs ----------
        // Native wipes every package morph and re-imports the whole
        // autoImportFolder tree (~7s per bank on this library) whenever the
        // loaded uid set moved at all, so one added or dropped character
        // package costs a full reparse of 10k packages. A set that only grew
        // has nothing stale: import just the added dirs, then run the native
        // tail (CompletePackageMorphAdd -> RebuildAllLookups) and adopt the
        // new uid sets. A set that shrank only needs the dropped packages
        // unlinked, which TryIncrementalMorphSync does in place. Every other
        // shape -- a retained package our fingerprint touched, an unresolved
        // member, a failed import -- falls back to the native call.

        private static bool MorphBankPrefix(object __instance, ref bool __result)
        {
            if (!Enabled || !_sceneRebuildDecided) return true;
            if (SceneLoadAccelerator.SceneLoadActive) return true;
            string folder = null;
            try
            {
                if (_morphBankAutoFolder != null)
                    folder = _morphBankAutoFolder.GetValue(__instance) as string;
            }
            catch { }
            if (string.IsNullOrEmpty(folder)) return true;
            // Only the standard morph roots are safe to shortcut.
            if (folder.Replace('/', '\\').ToLowerInvariant()
                    .IndexOf("person\\morphs", StringComparison.Ordinal) < 0)
                return true;
            _swMorph = System.Diagnostics.Stopwatch.StartNew();
            if (_localClassUnknown || _localClassMorphs || _needMorphs)
                return !TryIncrementalMorphSync(__instance, ref __result);
            // Nothing morph-related moved: report the same no-op verdict the
            // native set-equality early-out reaches, without paying for the
            // whole-library re-import it would otherwise run.
            if (!MorphBankInitialized(__instance)) return true;
            _skipMorph++;
            __result = false;
            return false;
        }

        private static Exception MorphBankFinalizer(Exception __exception)
        {
            if (_swMorph != null)
            {
                _msMorph += _swMorph.Elapsed.TotalMilliseconds;
                _nMorph++;
                _swMorph = null;
            }
            return __exception;
        }

        private static bool TryIncrementalMorphSync(object bank,
            ref bool result)
        {
            if (bank == null || _fmFindVarDirs == null ||
                _morphImportDir == null || _morphSubBankComplete == null ||
                _morphRebuildLookups == null || _getSubBanks == null ||
                _morphSubBankType == null || _varDirPackageProp == null ||
                _pkgUidProp == null || _pkgGroupProp == null ||
                _pkgGroupCustomOption == null || _morphBankAutoFolder == null ||
                _morphBankLoadedUids == null || _morphBankPreloadUids == null ||
                _morphPkgUidField == null || _morphStartValueField == null ||
                _morphValueProp == null || _subPackageMorphsProp == null ||
                _subBankRebuildDicts == null)
                return false;
            try
            {
                object listObj = _fmFindVarDirs.Invoke(null,
                    new object[] { _morphBankAutoFolder.GetValue(bank), true });
                var list = listObj as IList;
                if (list == null || list.Count == 0) return false;
                var newUids = new HashSet<string>();
                var newPreload = new HashSet<string>();
                var entries = new Dictionary<string, object>();
                for (int i = 0; i < list.Count; i++)
                {
                    object de = list[i];
                    object pkg = _varDirPackageProp.GetValue(de, null);
                    if (pkg == null) return false;
                    string uid = _pkgUidProp.GetValue(pkg, null) as string;
                    if (string.IsNullOrEmpty(uid)) return false;
                    newUids.Add(uid);
                    entries[uid] = de;
                    object grp = _pkgGroupProp.GetValue(pkg, null);
                    if (grp != null && (bool)_pkgGroupCustomOption.Invoke(grp,
                            new object[] { "preloadMorphs" }))
                        newPreload.Add(uid);
                }
                var loaded = _morphBankLoadedUids.GetValue(bank)
                    as HashSet<string>;
                var preload = _morphBankPreloadUids.GetValue(bank)
                    as HashSet<string>;
                if (loaded == null || preload == null) return false;
                var added = new List<string>();
                foreach (string u in newUids)
                    if (!loaded.Contains(u)) added.Add(u);
                var removed = new HashSet<string>();
                foreach (string u in loaded)
                    if (!newUids.Contains(u)) removed.Add(u);
                if (added.Count == 0 && removed.Count == 0) return false;
                HashSet<string> dirtyUids = DirtyPackageUids();
                // A retained package our diff touched is an in-place update --
                // only the native wipe reloads it. A uid the diff already
                // reports as gone is exempt: its absence from the listing is
                // the evidence, and the fingerprint cannot prove anything
                // about a file that no longer exists.
                if (dirtyUids != null)
                {
                    foreach (string u in dirtyUids)
                        if (loaded.Contains(u) && !removed.Contains(u))
                            return false;
                }
                if (added.Count > 0)
                {
                    if (dirtyUids == null) return false;
                    foreach (string u in added)
                        if (!dirtyUids.Contains(u)) return false;
                }
                // A preload option turned off for a package that is still
                // there would change how its morphs materialize, so only the
                // native reload may answer that.
                foreach (string u in preload)
                    if (!newPreload.Contains(u) && !removed.Contains(u))
                        return false;
                var subs = _getSubBanks.Invoke(bank,
                    new object[] { _morphSubBankType }) as object[];
                int purgePkgs = 0, purgeItems = 0, purgeZeroed = 0;
                if (removed.Count > 0)
                {
                    if (subs == null) return false;
                    for (int i = 0; i < subs.Length; i++)
                        purgeItems += PurgePackageMorphs(subs[i], removed,
                            ref purgeZeroed);
                    purgePkgs = removed.Count;
                }
                for (int i = 0; i < added.Count; i++)
                {
                    // Same arguments the native loop uses: preload packages
                    // import eagerly, everything else stays demand-loaded.
                    string u = added[i];
                    bool ok = (bool)_morphImportDir.Invoke(bank, new object[]
                        { entries[u], false, !newPreload.Contains(u), false });
                    if (!ok) return false;
                }
                if (subs != null)
                    for (int i = 0; i < subs.Length; i++)
                        _morphSubBankComplete.Invoke(subs[i], null);
                _morphRebuildLookups.Invoke(bank, null);
                _morphBankLoadedUids.SetValue(bank, newUids);
                _morphBankPreloadUids.SetValue(bank, newPreload);
                _incMorphPkgs += added.Count;
                _morphPurgePkgs += purgePkgs;
                _morphPurgeItems += purgeItems;
                _morphPurgeZeroed += purgeZeroed;
                result = true;
                return true;
            }
            catch (Exception e)
            {
                Log("形变增量同步失败，回退原生：" + e.Message);
                return false;
            }
        }

        // Unlinks the morphs of the dropped packages from one sub bank. The
        // value each morph carries has to leave the character with it --
        // native drops exactly these values too, because its save/restore
        // pass cannot re-find a deleted uid after the wipe -- and the sub
        // bank name/uid lookups are rebuilt so the morphs stop resolving.
        // Returns how many morphs were unlinked; zeroed counts the ones that
        // were carrying a live value.
        private static int PurgePackageMorphs(object subBank,
            HashSet<string> removed, ref int zeroed)
        {
            var morphs = _subPackageMorphsProp.GetValue(subBank, null) as IList;
            if (morphs == null || morphs.Count == 0) return 0;
            int count = 0;
            for (int i = morphs.Count - 1; i >= 0; i--)
            {
                object morph = morphs[i];
                if (morph == null) continue;
                string uid = _morphPkgUidField.GetValue(morph) as string;
                if (string.IsNullOrEmpty(uid) || !removed.Contains(uid))
                    continue;
                float start = (float)_morphStartValueField.GetValue(morph);
                if ((float)_morphValueProp.GetValue(morph, null) != start)
                {
                    _morphValueProp.SetValue(morph, start, null);
                    zeroed++;
                }
                morphs.RemoveAt(i);
                count++;
            }
            if (count > 0) _subBankRebuildDicts.Invoke(subBank, null);
            return count;
        }

        private static void ResolveClassGateMembers()
        {
            try
            {
                Type dcs = typeof(DAZCharacterSelector);
                _dynClothes = dcs.GetMethod("RefreshDynamicClothes", InstAll);
                _dynHair = dcs.GetMethod("RefreshDynamicHair", InstAll);
                _dcsFemaleClothingC = dcs.GetField("femaleClothingContainer",
                    InstAll);
                _dcsMaleClothingC = dcs.GetField("maleClothingContainer",
                    InstAll);
                _dcsFemaleHairC = dcs.GetField("femaleHairContainer", InstAll);
                _dcsMaleHairC = dcs.GetField("maleHairContainer", InstAll);
                _dcsInitClothes = dcs.GetMethod("InitClothingItems", InstAll);
                Type dynSel = typeof(SuperController).Assembly
                    .GetType("GenerateDAZDynamicSelectorUI");
                _dynUiResync = dynSel != null ? dynSel.GetMethod("Resync",
                    InstAll, null, Type.EmptyTypes, null) : null;
                _dynSelCharSelector = dynSel != null
                    ? dynSel.GetField("characterSelector", InstAll) : null;
                _dcsClothingMaleUi = dcs.GetField("clothingSelectorMaleUI",
                    InstAll);
                _syncItemsCacheEnable = dcs.GetMethod(
                    "EnableSyncCustomItemsCache", StaticAll, null,
                    Type.EmptyTypes, null);
                _syncItemsCacheDisable = dcs.GetMethod(
                    "DisableSyncCustomItemsCache", StaticAll, null,
                    Type.EmptyTypes, null);

                Type bank = typeof(DAZMorphBank);
                _morphBankType = bank;
                _morphBankRefresh = bank.GetMethod("RefreshPackageMorphs",
                    InstAll);
                _morphBankAutoFolder = bank.GetField("autoImportFolder",
                    InstAll);
                _morphBankLoadedUids = bank.GetField(
                    "currentLoadedMorphPackageUids", InstAll);
                _morphBankPreloadUids = bank.GetField(
                    "currentPreloadMorphPackageUids", InstAll);
                _morphRebuildLookups = bank.GetMethod("RebuildAllLookups",
                    InstAll, null, Type.EmptyTypes, null);
                foreach (MethodInfo m in bank.GetMethods(InstAll))
                {
                    if (m.Name != "RuntimeImportFromDir") continue;
                    ParameterInfo[] ps = m.GetParameters();
                    if (ps.Length != 4) continue;
                    if (ps[0].ParameterType.Name != "DirectoryEntry") continue;
                    if (ps[1].ParameterType != typeof(bool)) continue;
                    _morphImportDir = m;
                    break;
                }
                _morphSubBankType = typeof(DAZMorphSubBank);
                _morphSubBankComplete = _morphSubBankType.GetMethod(
                    "CompletePackageMorphAdd", InstAll);
                _getSubBanks = typeof(Component).GetMethod(
                    "GetComponentsInChildren", new[] { typeof(Type) });
                Type morph = typeof(DAZMorph);
                _morphPkgUidField = morph.GetField("packageUid", InstAll);
                _morphStartValueField = morph.GetField("startValue", InstAll);
                _morphValueProp = morph.GetProperty("morphValue", InstAll);
                _subPackageMorphsProp = _morphSubBankType.GetProperty(
                    "packageMorphs", InstAll);
                _subBankRebuildDicts = _morphSubBankType.GetMethod(
                    "RebuildMorphsByNameAndUid", InstAll);

                Assembly asm = typeof(SuperController).Assembly;
                Type fm = asm.GetType("MVR.FileManagement.FileManager");
                _fmFindVarDirs = fm == null ? null : fm.GetMethod(
                    "FindVarDirectories",
                    BindingFlags.Static | BindingFlags.Public |
                    BindingFlags.NonPublic,
                    null, new[] { typeof(string), typeof(bool) }, null);
                Type vde = asm.GetType("MVR.FileManagement.VarDirectoryEntry");
                Type vp = asm.GetType("MVR.FileManagement.VarPackage");
                Type vpg = asm.GetType("MVR.FileManagement.VarPackageGroup");
                _varDirPackageProp = vde == null ? null
                    : vde.GetProperty("Package", InstAll);
                _pkgUidProp = vp == null ? null
                    : vp.GetProperty("Uid", InstAll);
                _pkgGroupProp = vp == null ? null
                    : vp.GetProperty("Group", InstAll);
                _pkgGroupCustomOption = vpg == null ? null
                    : vpg.GetMethod("GetCustomOption", InstAll, null,
                        new[] { typeof(string) }, null);
            }
            catch (Exception e)
            {
                Log("类别门控成员解析异常：" + e.Message);
            }
        }

        // ---------- fingerprints ----------

        private static string Norm(string path)
        {
            try { return Path.GetFullPath(path); }
            catch { return path; }
        }

        private static string Stat(string file)
        {
            return StatInfo(new FileInfo(file), file);
        }

        // Same value as the old inline body, but the FileInfo can come from a
        // directory enumeration buffer (Length/LastWriteTimeUtc/Attributes are
        // filled from the FindFirstFile data), so one per-file stat syscall
        // per entry disappears. Verified value-identical on this install.
        private static string StatInfo(FileInfo fi, string path)
        {
            if ((fi.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                string link = LinkStatCached(path);
                if (link != null) return link;
            }
            return fi.Length + "|" + fi.LastWriteTimeUtc.Ticks;
        }

        // VAR-manager maps packages as file symlinks and rebuilds the mapping
        // on Update — the link's own mtime churns while the package behind it
        // is untouched (one Update dirtied ~10k unchanged entries). Worse, the
        // link reports Length=0, so a replaced target would go undetected.
        // Fingerprint the resolved target instead: final path + real size +
        // real write time. Dangling links fall back to the link's own stat.
        [System.Runtime.InteropServices.DllImport("kernel32.dll",
            CharSet = System.Runtime.InteropServices.CharSet.Unicode,
            SetLastError = true)]
        private static extern IntPtr CreateFileW(string lpFileName,
            uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
            uint dwCreationDisposition, uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);
        [System.Runtime.InteropServices.DllImport("kernel32.dll",
            CharSet = System.Runtime.InteropServices.CharSet.Unicode,
            SetLastError = true)]
        private static extern uint GetFinalPathNameByHandleW(IntPtr hFile,
            StringBuilder lpszFilePath, uint cchFilePath, uint dwFlags);
        [System.Runtime.InteropServices.DllImport("kernel32.dll",
            SetLastError = true)]
        private static extern bool GetFileTime(IntPtr hFile,
            out long lpCreationTime, out long lpLastAccessTime,
            out long lpLastWriteTime);
        [System.Runtime.InteropServices.DllImport("kernel32.dll",
            SetLastError = true)]
        private static extern bool GetFileSizeEx(IntPtr hFile,
            out long lpFileSize);
        [System.Runtime.InteropServices.DllImport("kernel32.dll",
            SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        private static readonly IntPtr InvalidFileHandle = new IntPtr(-1);

        private static string LinkTargetStat(string file)
        {
            // No FILE_FLAG_OPEN_REPARSE_POINT: the handle follows the link,
            // so GetFinalPathNameByHandle/GetFileTime describe the target.
            IntPtr h = CreateFileW(file, 0x80 /*FILE_READ_ATTRIBUTES*/, 0x7,
                IntPtr.Zero, 3 /*OPEN_EXISTING*/,
                0x02000000 /*FILE_FLAG_BACKUP_SEMANTICS*/, IntPtr.Zero);
            if (h == InvalidFileHandle || h == IntPtr.Zero) return null;
            try
            {
                var sb = new StringBuilder(2048);
                uint len = GetFinalPathNameByHandleW(h, sb,
                    (uint)sb.Capacity, 0);
                if (len == 0 || len >= (uint)sb.Capacity) return null;
                long size, created, accessed, written;
                if (!GetFileSizeEx(h, out size)) size = -1L;
                if (!GetFileTime(h, out created, out accessed, out written))
                    written = 0L;
                return "lnk:" + sb.ToString() + "|" + size + "|" + written;
            }
            finally { CloseHandle(h); }
        }

        // The var tree is ~10k symlinks into the tidied store on another
        // volume; resolving each one costs 5 syscalls (open + final path +
        // size + time + close), i.e. ~2s per pass. The resolved target is
        // stable while the package behind it is untouched, so keep the
        // previous pass's target and re-stat it directly (1 syscall). A
        // failed stat (target moved/deleted) falls back to full resolution,
        // which also re-seeds the entry. Fingerprint values stay identical:
        // both routes emit "lnk:<target>|<size>|<writeTime>".
        private static Dictionary<string, string> _linkTargets;
        private static int _lkAttrHits, _lkFallbacks;

        private static void SeedLinkTargets(Dictionary<string, string> prev)
        {
            _linkTargets = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);
            _lkAttrHits = 0; _lkFallbacks = 0;
            if (prev == null) return;
            foreach (var kv in prev)
                if (kv.Value != null &&
                    kv.Value.StartsWith("lnk:", StringComparison.Ordinal))
                    _linkTargets[kv.Key] = kv.Value;
        }

        private static string LinkCacheStat()
        {
            return "链接目标复用 " + _lkAttrHits + " 回退解析 " + _lkFallbacks;
        }

        private static string LinkStatCached(string linkPath)
        {
            // Cache keys come from the fingerprint map, which stores Norm()'d
            // paths; the walkers hand over the raw enumerated path, so
            // normalize the same way before looking up.
            string key = Norm(linkPath);
            string cached;
            if (_linkTargets != null &&
                _linkTargets.TryGetValue(key, out cached))
            {
                string target = LinkTargetOfValue(cached);
                if (target != null)
                {
                    string viaAttr = TargetStat(target);
                    if (viaAttr != null) { _lkAttrHits++; return viaAttr; }
                }
            }
            _lkFallbacks++;
            string full = LinkTargetStat(linkPath);
            if (full != null && _linkTargets != null &&
                full.StartsWith("lnk:", StringComparison.Ordinal))
                _linkTargets[key] = full;
            return full;
        }

        private static string LinkTargetOfValue(string v)
        {
            if (v == null || !v.StartsWith("lnk:", StringComparison.Ordinal))
                return null;
            int b = v.LastIndexOf('|');
            if (b <= 4) return null;
            int a = v.LastIndexOf('|', b - 1);
            if (a <= 4) return null;
            return v.Substring(4, a - 4);
        }

        [System.Runtime.InteropServices.StructLayout(
            System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct Win32FileTime { public uint Low; public uint High; }
        [System.Runtime.InteropServices.StructLayout(
            System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct Win32FileAttrData
        {
            public uint Attributes;
            public Win32FileTime Creation;
            public Win32FileTime Access;
            public Win32FileTime Write;
            public uint SizeHigh;
            public uint SizeLow;
        }
        [System.Runtime.InteropServices.DllImport("kernel32.dll",
            CharSet = System.Runtime.InteropServices.CharSet.Unicode,
            SetLastError = true)]
        private static extern bool GetFileAttributesExW(string path,
            int infoLevel, out Win32FileAttrData data);

        private static string TargetStat(string target)
        {
            Win32FileAttrData d;
            if (!GetFileAttributesExW(target, 0, out d)) return null;
            long size = ((long)d.SizeHigh << 32) | d.SizeLow;
            long written = ((long)d.Write.High << 32) | d.Write.Low;
            return "lnk:" + target + "|" + size + "|" + written;
        }

        // Mono's DirectoryInfo.GetFileSystemInfos() re-stats every entry — on
        // this install it measured *slower* than Directory.GetFiles (22.6k
        // files 1.8s -> 2.3s), so the walkers read the WIN32_FIND_DATA fields
        // straight out of the find handle instead: one enumeration stream per
        // directory, no per-entry stat at all. Values are the same as
        // "Length + LastWriteTimeUtc.Ticks" (verified entry-for-entry).
        private const long FileTimeToDateTimeTicks = 504911232000000000L;
        private const uint AttrDirectoryMask = 0x10u;
        private const uint AttrReparsePointMask = 0x400u;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode,
            SetLastError = true)]
        private static extern IntPtr FindFirstFileW(string pattern,
            IntPtr findData);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode,
            SetLastError = true)]
        private static extern bool FindNextFileW(IntPtr handle,
            IntPtr findData);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FindClose(IntPtr handle);

        private static readonly IntPtr FindInvalidHandle = new IntPtr(-1);

        // WIN32_FIND_DATAW: attributes@0, lastWriteTime@20, sizeHigh@28,
        // sizeLow@32, cFileName@44 (260 wide chars).
        private const int FindDataSize = 600;
        private const int FindDataWriteTimeOffset = 20;
        private const int FindDataSizeHighOffset = 28;
        private const int FindDataSizeLowOffset = 32;
        private const int FindDataNameOffset = 44;

        private static string FindName(IntPtr buf)
        {
            return Marshal.PtrToStringUni(new IntPtr(
                buf.ToInt64() + FindDataNameOffset));
        }

        private static long FindSize(IntPtr buf)
        {
            return ((long)Marshal.ReadInt32(buf, FindDataSizeHighOffset) << 32) |
                (uint)Marshal.ReadInt32(buf, FindDataSizeLowOffset);
        }

        private static string StatFind(string path, uint attrs, long size,
            long fileTime)
        {
            if ((attrs & AttrReparsePointMask) != 0)
            {
                string link = LinkStatCached(path);
                if (link != null) return link;
            }
            return size + "|" + (fileTime + FileTimeToDateTimeTicks);
        }

        // Iterative, bounded enumeration. This install maps .var files into
        // AddonPackages via VAR-manager links, so directory junctions are
        // real content and must be traversed — but a junction loop (or
        // naive recursion on it) overflows the stack and hard-crashes the
        // process. An explicit stack can't overflow; the depth cap stops
        // link cycles (each loop iteration appends the link name, so the
        // path also hits the Win32 length limit and dies on its own), and
        // the entry cap bounds worst-case cost.
        private const int MaxDepth = 40;
        private const int MaxEntries = 400000;
        private static bool _truncated;   // enum hit a cap — never trust the map

        private static void EnumTree(Dictionary<string, string> map,
            string root, string[] patterns, bool stripDisabledSuffix,
            string[] skipPrefixes)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return;
            var stack = new Stack<KeyValuePair<string, int>>();
            stack.Push(new KeyValuePair<string, int>(root, 0));
            while (stack.Count > 0)
            {
                if (map.Count >= MaxEntries) { _truncated = true; return; }
                var kv = stack.Pop();
                string dir = kv.Key;
                int depth = kv.Value;
                string nd = Norm(dir);
                bool skip = false;
                if (skipPrefixes != null)
                    foreach (string sp in skipPrefixes)
                        if (nd.Equals(sp, StringComparison.OrdinalIgnoreCase) ||
                            nd.StartsWith(sp + Path.DirectorySeparatorChar,
                                StringComparison.OrdinalIgnoreCase))
                        { skip = true; break; }
                if (skip) continue;

                // One FindFirstFile stream per directory: name + size + mtime
                // + attributes come back together, so there is no per-entry
                // stat. Junction/symlink dirs are followed on purpose — the
                // var-manager setup keeps real packages behind mappings, and
                // loops terminate at MaxDepth or the Win32 path limit.
                IntPtr buf = Marshal.AllocHGlobal(FindDataSize);
                try
                {
                    IntPtr hFind = FindFirstFileW(Path.Combine(dir, "*"), buf);
                    if (hFind != FindInvalidHandle)
                    {
                        try
                        {
                            do
                            {
                                string name = FindName(buf);
                                if (name == null || name == "." ||
                                    name == "..") continue;
                                uint attrs = (uint)Marshal.ReadInt32(buf, 0);
                                long size = FindSize(buf);
                                long ft = Marshal.ReadInt64(buf,
                                    FindDataWriteTimeOffset);
                                string f = Path.Combine(dir, name);
                                if ((attrs & AttrDirectoryMask) != 0)
                                {
                                    if (depth < MaxDepth)
                                        stack.Push(new KeyValuePair<
                                            string, int>(f, depth + 1));
                                    continue;
                                }
                                if (patterns != null)
                                {
                                    bool match = false;
                                    string lf = f.ToLowerInvariant();
                                    foreach (string p in patterns)
                                        if (lf.EndsWith(p,
                                            StringComparison.Ordinal))
                                        { match = true; break; }
                                    if (!match) continue;
                                }
                                try
                                {
                                    string key = f;
                                    if (stripDisabledSuffix &&
                                        key.EndsWith(".disabled",
                                        StringComparison.OrdinalIgnoreCase))
                                        key = key.Substring(0, key.Length -
                                            ".disabled".Length);
                                    map[Norm(key)] = StatFind(f, attrs,
                                        size, ft);
                                }
                                catch { }
                            } while (FindNextFileW(hFind, buf));
                        }
                        finally { FindClose(hFind); }
                    }
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
        }

        private static void AddPattern(Dictionary<string, string> map,
            string root, string pattern, bool stripDisabledSuffix)
        {
            EnumTree(map, root, new[] { pattern.Replace("*", "") },
                stripDisabledSuffix, null);
        }

        private static void AddTree(Dictionary<string, string> map,
            string root, string[] skipPrefixes)
        {
            EnumTree(map, root, null, false, skipPrefixes);
        }

        private static Dictionary<string, string> VarFingerprint()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            // Reuse the previous pass's resolved link targets (they are in the
            // cached fingerprint map) so ~10k symlinks cost one cheap stat
            // each instead of five syscalls.
            SeedLinkTargets(_varCache);
            // One traversal for all var extensions — AddonPackages holds
            // ~10k entries behind junctions, each extra pass costs seconds.
            // Patterns must be lowercase: EnumTree lowercases file names.
            EnumTree(map, "AddonPackages",
                new[] { ".var", ".var.disabled", ".var.batempvar" },
                true, null);
            AddPattern(map, _offloadRoot, "*.var", false);
            // VaM-side per-var user prefs are re-read by the rescan — include
            // them so toggling prefs/metascore still triggers the real scan.
            AddTree(map, "AddonPackagesFilePrefs", null);
            AddTree(map, "AddonPackagesUserPrefs", null);
            // VAR-management state is not on disk in the trees above — fold
            // it in so toggling management or switching rulesets dirties the
            // fingerprint and forces a real scan (the enable/disable dance
            // and ruleset application live inside RescanPackages).
            map["<mgmt>"] = VarMgmtSignature();
            // Disabled vars get their meta extracted during the rescan dance;
            // if the cache was wiped externally the rescan must run.
            if (!string.IsNullOrEmpty(_metaCacheRoot))
            {
                long h = 0, c = 0;
                HashTree(_metaCacheRoot, null, ref h, ref c);
                map["<metacache>"] = c + ":" + h;
            }
            // BA's persisted settings (rulesets included) — edits must dirty
            // the fingerprint or a skipped rescan would leave them unapplied.
            if (!string.IsNullOrEmpty(_baSettingsFile) &&
                File.Exists(_baSettingsFile))
                map["<settings>"] = Stat(_baSettingsFile);
            return map;
        }

        private static string VarMgmtSignature()
        {
            try
            {
                if (!VarMgmtActive()) return "off";
                string ruleset = "?";
                object jse = _varMgmtRuleSet == null
                    ? null : _varMgmtRuleSet.GetValue(null);
                if (jse != null)
                {
                    var p = jse.GetType().GetProperty("mainVal");
                    if (p != null)
                        ruleset = p.GetValue(jse, null) as string ?? "?";
                }
                object list = _varMgmtRuleSetList == null
                    ? null : _varMgmtRuleSetList.GetValue(null);
                int rules = CollectionCount(list);
                return "on|" + ruleset + "|" + rules;
            }
            catch { return "err"; }
        }

        // Local fingerprint as a rolling order-independent hash: for each
        // file mix (path + size + mtime) into one long, add to accumulator.
        // Zero retained allocations — the Boehm GC heap that killed the
        // per-file dictionary is untouched.
        private static void LocalFingerprint(out long[] hashes, out long[] counts,
            Dictionary<string, long> fileMap)
        {
            // PluginData holds BA's own data files (favourites/hidden flags)
            // which change on every UI interaction — including them would
            // keep the fingerprint permanently dirty. They are not local
            // resources and their edits go through BA's own manifest anyway.
            string savesRoot = Norm("Saves");
            var skip = new[]
            {
                Path.Combine(savesRoot, "PluginData"),
                Path.Combine(savesRoot, "screenshots"),
            };
            var roots = LocalRoots();
            hashes = new long[roots.Count];
            counts = new long[roots.Count];
            for (int i = 0; i < roots.Count; i++)
            {
                long h = 0, c = 0;
                HashTree(roots[i], skip, ref h, ref c, fileMap);
                hashes[i] = h; counts[i] = c;
            }
        }

        private static void HashTree(string root, string[] skipPrefixes,
            ref long hash, ref long count)
        {
            HashTree(root, skipPrefixes, ref hash, ref count, null);
        }

        private static void HashTree(string root, string[] skipPrefixes,
            ref long hash, ref long count, Dictionary<string, long> fileMap)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return;
            var stack = new Stack<KeyValuePair<string, int>>();
            stack.Push(new KeyValuePair<string, int>(root, 0));
            while (stack.Count > 0)
            {
                if (count >= MaxEntries) { _truncated = true; return; }
                var kv = stack.Pop();
                string dir = kv.Key;
                int depth = kv.Value;
                string nd = Norm(dir);
                bool skip = false;
                if (skipPrefixes != null)
                foreach (string sp in skipPrefixes)
                    if (nd.Equals(sp, StringComparison.OrdinalIgnoreCase) ||
                        nd.StartsWith(sp + Path.DirectorySeparatorChar,
                            StringComparison.OrdinalIgnoreCase))
                    { skip = true; break; }
                if (skip) continue;

                IntPtr buf = Marshal.AllocHGlobal(FindDataSize);
                try
                {
                    IntPtr hFind = FindFirstFileW(Path.Combine(dir, "*"), buf);
                    if (hFind != FindInvalidHandle)
                    {
                        try
                        {
                            do
                            {
                                string name = FindName(buf);
                                if (name == null || name == "." ||
                                    name == "..") continue;
                                uint attrs = (uint)Marshal.ReadInt32(buf, 0);
                                long size = FindSize(buf);
                                long ft = Marshal.ReadInt64(buf,
                                    FindDataWriteTimeOffset);
                                string f = Path.Combine(dir, name);
                                if ((attrs & AttrDirectoryMask) != 0)
                                {
                                    if (depth < MaxDepth)
                                        stack.Push(new KeyValuePair<
                                            string, int>(f, depth + 1));
                                    continue;
                                }
                                try
                                {
                                    // FNV-1a over path chars, then mix
                                    // size+mtime.
                                    long h = unchecked((long)1469598103934665603UL);
                                    for (int i = 0; i < f.Length; i++)
                                    {
                                        h ^= char.ToLowerInvariant(f[i]);
                                        h *= 1099511628211L;
                                    }
                                    h ^= size; h *= 1099511628211L;
                                    h ^= (ft + FileTimeToDateTimeTicks);
                                    h *= 1099511628211L;
                                    hash += h;
                                    count++;
                                    if (fileMap != null)
                                        fileMap[Norm(f)] = h;
                                }
                                catch { }
                            } while (FindNextFileW(hFind, buf));
                        }
                        finally { FindClose(hFind); }
                    }
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
        }

        private static List<string> LocalRoots()
        {
            try
            {
                if (_localRootsMethod != null)
                {
                    var roots = _localRootsMethod.Invoke(null, null) as IEnumerable;
                    if (roots != null)
                    {
                        var list = new List<string>();
                        foreach (object r in roots)
                            if (r is string) list.Add((string)r);
                        if (list.Count > 0) return list;
                    }
                }
            }
            catch { }
            return new List<string>
            {
                "Saves", "Custom\\Atom", "Custom\\Scripts", "Custom\\SubScene",
                "Custom\\Assets", "Custom\\Clothing\\Female", "Custom\\Clothing\\Male",
                "Custom\\Hair\\Female", "Custom\\Hair\\Male", "Custom\\Sounds",
                "Custom\\Audio", "Custom\\PluginPresets",
            };
        }

        private static HashSet<string> DirtyVarPaths(
            Dictionary<string, string> cur, Dictionary<string, string> prev)
        {
            var dirty = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in cur)
            {
                // Only var archive paths gate RescanVARResources; pref files
                // share the fingerprint map but never map onto varFilePathName.
                if (!kv.Key.EndsWith(".var", StringComparison.OrdinalIgnoreCase))
                    continue;
                string v;
                if (prev == null || !prev.TryGetValue(kv.Key, out v) || v != kv.Value)
                    dirty.Add(kv.Key);
            }
            // Only the old map can name a removed archive. Leaving it out
            // made a pure removal produce an empty dirty set, which every
            // gate below then read as "nothing happened".
            if (prev != null)
                foreach (var kv in prev)
                {
                    if (!kv.Key.EndsWith(".var",
                            StringComparison.OrdinalIgnoreCase)) continue;
                    if (cur.ContainsKey(kv.Key)) continue;
                    dirty.Add(kv.Key);
                }
            return dirty;
        }

        private static bool Equal(Dictionary<string, string> a,
            Dictionary<string, string> b)
        {
            if (a.Count != b.Count) return false;
            foreach (var kv in a)
            {
                string v;
                if (!b.TryGetValue(kv.Key, out v) || v != kv.Value) return false;
            }
            return true;
        }

        private static int CollectionCount(object col)
        {
            if (col == null) return -1;
            // HashSet<T> does not implement the non-generic ICollection —
            // read Count off the runtime type instead of casting.
            var p = col.GetType().GetProperty("Count");
            if (p == null) return -1;
            try { return (int)p.GetValue(col, null); }
            catch { return -1; }
        }

        private static bool VarManifestPopulated()
        {
            try
            {
                return CollectionCount(
                    _availableVars.GetValue(null, null)) > 0;
            }
            catch { return false; }
        }

        private static bool LocalPopulated()
        {
            try
            {
                return CollectionCount(_localRvges.GetValue(null)) > 0;
            }
            catch { return false; }
        }

        private static void InvokePostRescan()
        {
            try { _postRescan.Invoke(null, null); }
            catch (Exception e) { Log("PostRescanRefresh 调用失败：" + e.Message); }
        }

        // ---------- cache persistence ----------

        // One-off warm-up of the category memory. Packages that were already
        // installed before this map existed have no verdict, and a package
        // that is being deleted can no longer be listed (the file is gone),
        // so the verdicts are filled from what is still on disk. This runs
        // once at patch install — where a few seconds are invisible next to
        // VaM's own start-up — and each later install continues where the
        // last one stopped; a complete map makes it a no-op.
        private const int SeedBudgetMs = 20000;

        private static void SeedKindMemory()
        {
            try
            {
                if (_varCache == null || _varCache.Count == 0) return;
                if (_varKindCache == null)
                    _varKindCache = new Dictionary<string, string>(
                        StringComparer.OrdinalIgnoreCase);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                int done = 0, capped = 0;
                BaDirtyContentProbe.Quiet = true;
                try
                {
                    foreach (var kv in _varCache)
                    {
                        if (!kv.Key.EndsWith(".var",
                                StringComparison.OrdinalIgnoreCase)) continue;
                        if (kv.Key.EndsWith(".var.batempvar",
                                StringComparison.OrdinalIgnoreCase)) continue;
                        if (_varKindCache.ContainsKey(kv.Key)) continue;
                        if (sw.ElapsedMilliseconds > SeedBudgetMs)
                        { capped = 1; break; }
                        try
                        {
                            if (!File.Exists(kv.Key) &&
                                !File.Exists(kv.Key + ".disabled")) continue;
                            List<string> files =
                                BaDirtyContentProbe.PackageEntries(kv.Key);
                            if (files == null || files.Count == 0) continue;
                            bool c = false, h = false, m = false, b = false;
                            KindFlagsOf(files, ref c, ref h, ref m, ref b);
                            RememberVarKind(kv.Key, c, h, m, b);
                            done++;
                        }
                        catch { }
                    }
                }
                finally { BaDirtyContentProbe.Quiet = false; }
                if (done > 0)
                {
                    SaveCache();
                    Log("类别记忆补种：" + done + " 包，用时 " +
                        sw.ElapsedMilliseconds + "ms，共 " + _varKindCache.Count +
                        " 条" + (capped > 0 ? "（本轮达时限，下次安装继续）" : ""));
                }
            }
            catch (Exception e) { Log("类别记忆补种失败：" + e.Message); }
        }

        private static void LoadCache()
        {
            if (_cacheLoaded) return;
            _cacheLoaded = true;
            try
            {
                if (!File.Exists(CachePath)) return;
                JSONClass root = JSON.Parse(File.ReadAllText(CachePath)).AsObject;
                _varCache = ReadSection(root["var"].AsObject);
                _varKindCache = ReadSection(root["kind"].AsObject);
                JSONClass local = root["local"].AsObject;
                if (local != null)
                {
                    string v = local["v"].Value;
                    if (!string.IsNullOrEmpty(v))
                    {
                        var parts = v.Split(';');
                        _localHashCache = new long[parts.Length];
                        _localCountCache = new long[parts.Length];
                        for (int i = 0; i < parts.Length; i++)
                        {
                            var hc = parts[i].Split(',');
                            _localHashCache[i] = long.Parse(hc[0]);
                            _localCountCache[i] = long.Parse(hc[1]);
                        }
                        JSONClass files = local["files"].AsObject;
                        if (files != null)
                        {
                            _localFileCache = new Dictionary<string, long>(
                                StringComparer.OrdinalIgnoreCase);
                            foreach (KeyValuePair<string, JSONNode> kv
                                in files)
                                _localFileCache[kv.Key] =
                                    long.Parse(kv.Value.Value);
                        }
                        _localCacheSet = true;
                    }
                }
            }
            catch { _varCache = null; _varKindCache = null;
                    _localCacheSet = false; }
        }

        private static Dictionary<string, string> ReadSection(JSONClass node)
        {
            if (node == null) return null;
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, JSONNode> kv in node)
                map[kv.Key] = kv.Value.Value;
            return map;
        }

        // Streamed write — materializing ~12k entries through SimpleJSON
        // (a node per entry + recursive ToString) allocates tens of MB of
        // transients, enough to tip a fragmented Boehm heap into the fatal
        // 0x80000003 seen on the very first early-commit.
        private static void SaveCache()
        {
            try
            {
                using (var w = new StreamWriter(CachePath, false))
                {
                    w.Write("{\"var\":{");
                    bool first = true;
                    if (_varCache != null)
                        foreach (var kv in _varCache)
                        {
                            if (!first) w.Write(", ");
                            first = false;
                            w.Write('"'); EscJson(w, kv.Key);
                            w.Write("\":\""); EscJson(w, kv.Value);
                            w.Write('"');
                        }
                    w.Write('}');
                    if (_varKindCache != null && _varKindCache.Count > 0)
                    {
                        w.Write(",\"kind\":{");
                        first = true;
                        foreach (var kv in _varKindCache)
                        {
                            if (!first) w.Write(", ");
                            first = false;
                            w.Write('"'); EscJson(w, kv.Key);
                            w.Write("\":\""); EscJson(w, kv.Value);
                            w.Write('"');
                        }
                        w.Write('}');
                    }
                    if (_localCacheSet)
                    {
                        w.Write(",\"local\":{\"v\":\"");
                        for (int i = 0; i < _localHashCache.Length; i++)
                        {
                            if (i > 0) w.Write(';');
                            w.Write(_localHashCache[i]); w.Write(',');
                            w.Write(_localCountCache[i]);
                        }
                        w.Write('\"');
                        if (_localFileCache != null)
                        {
                            w.Write(",\"files\":{");
                            first = true;
                            foreach (var kv in _localFileCache)
                            {
                                if (!first) w.Write(", ");
                                first = false;
                                w.Write('"'); EscJson(w, kv.Key);
                                w.Write("\":\"");
                                w.Write(kv.Value.ToString(
                                    System.Globalization.CultureInfo
                                        .InvariantCulture));
                                w.Write('"');
                            }
                            w.Write('}');
                        }
                        w.Write('}');
                    }
                    w.Write('}');
                }
            }
            catch (Exception e) { Log("指纹缓存写入失败：" + e.Message); }
        }

        private static void EscJson(StreamWriter w, string s)
        {
            foreach (char c in s)
            {
                if (c == '\\' || c == '"') w.Write('\\');
                w.Write(c);
            }
        }

        private static JSONClass WriteSection(Dictionary<string, string> map)
        {
            var node = new JSONClass();
            if (map != null)
                foreach (var kv in map) node[kv.Key] = kv.Value;
            return node;
        }
    }
}
