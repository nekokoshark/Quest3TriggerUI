using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Quest3TriggerUI
{
    // Timing attribution for the "clothing editor open stalls" complaint:
    //   1) click -> editor visible: UIAssist's own build cost, measured by
    //      patch timers on GridsDisplay.CreateUIButtons / RefreshACE /
    //      RefreshACEAndScrollbars (installed lazily once the JayJayWon
    //      assembly is resolvable).
    //   2) editor visible -> side bars appear: our build path, timed per
    //      phase in UpdatePresetButtons + per tick in the cell pumps.
    // Each run logs one line; steady state stays silent.
    internal static partial class UiAssistHudLink
    {
        private static Harmony _aceProbeHarmony;
        private static bool _aceProbeTried;

        private static long Mark()
        {
            return System.Diagnostics.Stopwatch.GetTimestamp();
        }

        private static long ElapsedMs(long since)
        {
            if (since == 0L) return 0L;
            return (System.Diagnostics.Stopwatch.GetTimestamp() - since) *
                1000L / System.Diagnostics.Stopwatch.Frequency;
        }

        // Polled from Observe; AssemblyCatalog caches the assembly scan for
        // a second so this costs ~nothing once UIAssist is present.
        private static void EnsureAceOpenProbe()
        {
            if (_aceProbeHarmony != null || _aceProbeTried) return;
            Assembly asm = AssemblyCatalog.FindByType("JayJayWon.GridsDisplay");
            if (asm == null) return;
            _aceProbeTried = true;
            try
            {
                _aceProbeHarmony = new Harmony("Quest3TriggerUI.ace-open-probe");
                // CreateUIButtons lives on GridsDisplay; RefreshACE on the
                // editor object (ActiveClothingEditorDisplay) — probe both
                // candidate types so a refactor only loses one timer.
                foreach (string tn in new string[] {
                    "JayJayWon.GridsDisplay",
                    "JayJayWon.ActiveClothingEditorDisplay" })
                {
                    Type t = asm.GetType(tn);
                    if (t == null) continue;
                    PatchAceTimer(t, "CreateUIButtons");
                    PatchAceTimer(t, "RefreshACE");
                    PatchAceTimer(t, "RefreshACEAndScrollbars");
                }
            }
            catch (Exception e) { Error(e); }
        }

        private static void PatchAceTimer(Type type, string name)
        {
            MethodInfo m = type.GetMethod(name, Flags,
                null, Type.EmptyTypes, null);
            if (m == null) return;
            _aceProbeHarmony.Patch(m,
                prefix: new HarmonyMethod(
                    typeof(UiAssistHudLink).GetMethod("AceProbePrefix", Flags)),
                postfix: new HarmonyMethod(
                    typeof(UiAssistHudLink).GetMethod("AceProbePostfix", Flags)));
        }

        private static void AceProbePrefix(out long __state)
        {
            __state = System.Diagnostics.Stopwatch.GetTimestamp();
        }

        private static void AceProbePostfix(MethodBase __originalMethod,
            long __state)
        {
            Log("[ACE] native " + __originalMethod.Name + " " +
                ElapsedMs(__state) + "ms");
        }
    }
}
