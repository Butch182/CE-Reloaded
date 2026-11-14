using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace CEReloaded
{
    // Why this exists: CPU load and fan noise went up as soon as one of this mod's large windows was
    // open, and to the maximum in the Assign window.
    //
    // The math, every link verified in RimWorld's decompiled code, none of it guessed:
    //   * Verse.Root.CheckGlobalInit sets Application.targetFrameRate to the monitor's refresh rate.
    //     On a 143 Hz monitor RimWorld therefore draws 143 frames per second, even while the game is
    //     paused and nothing moves on screen.
    //   * Verse.Window.WindowOnGUI calls the window content via GUI.Window. Unity's IMGUI calls the
    //     window function AT LEAST twice per frame (Layout and Repaint) plus once per input event.
    //   * Our most expensive window content costs a measured ~3.4 ms per pass.
    //     3.4 ms * 2 passes * 143 frames = roughly 1000 ms of CPU time per second - one core at 100 %,
    //     permanently, for a picture that stands still. That is exactly what the fan reacts to.
    //
    // The lever here is the one that works regardless of content: as long as a window of this mod is
    // open AND the game is paused because of it, there is no reason for 143 frames per second.
    // 60 while interacting, 30 when neither mouse nor key has done anything for three quarters of a
    // second. That is the same work per frame as before - just a quarter to half as often.
    //
    // Deliberately NOT tied to individual windows, but to a per-frame check: a window that forgets to
    // release across PostClose/Close (crash in the content, save loaded while it is open) would
    // otherwise leave the game stuck at 30 frames permanently. This version heals itself - as soon as
    // none of our windows is open, the original value is back on the next frame.
    [HarmonyPatch(typeof(WindowStack), nameof(WindowStack.WindowsUpdate))]
    public static class Patch_IdleFrameRate
    {
        public static void Postfix()
        {
            IdleFrameRate.Update();
        }
    }

    public static class IdleFrameRate
    {
        // While interacting: 60 is smooth for a paused, static UI - even when dragging a
        // slider. When idle: 30, nothing moves at all.
        private const int ActiveFps = 60;
        private const int IdleFps = 30;
        private const float IdleAfterSeconds = 0.75f;

        private static readonly Assembly OwnAssembly = typeof(IdleFrameRate).Assembly;

        private static bool held;            // we are currently holding the frame rate (and know the original value)
        private static int savedTarget;
        private static int appliedFps = -1;  // what was actually set last, so we don't write every frame
        private static Vector3 lastMouse;
        private static float lastInputAt;

        public static void Update()
        {
            // Don't touch it at all when vSync is on (flickering while the fan was running, with the
            // measurement line showing "140 fps (target 30, vSync 1)").
            // Unity ignores targetFrameRate as soon as vSyncCount > 0 - the 140 frames at "target 30" are the
            // proof. Switching 30/60 depending on input therefore never throttled anything and was at the same
            // time the ONLY value this mod still changed about frame output. A lever without effect that
            // remains a flicker candidate goes away: under vSync this patch no longer does anything.
            // Without vSync (turned off by the player) it keeps throttling as before - there the value works.
            // Release() instead of return, so a value we set before a vSync change does not
            // stay behind.
            if (QualitySettings.vSyncCount > 0 || !ShouldHold())
            {
                Release();
                return;
            }

            if (!held)
            {
                savedTarget = Application.targetFrameRate;
                held = true;
                appliedFps = -1;
                lastMouse = Input.mousePosition;
                lastInputAt = Time.realtimeSinceStartup;
            }

            // Mouse or key = "someone is working with it". The time window afterwards covers the pauses
            // between two clicks, so the frame rate doesn't jump every time the hand comes off.
            Vector3 mouse = Input.mousePosition;
            if (mouse != lastMouse || Input.anyKey || Input.mouseScrollDelta.y != 0f)
            {
                lastMouse = mouse;
                lastInputAt = Time.realtimeSinceStartup;
            }
            bool idle = Time.realtimeSinceStartup - lastInputAt > IdleAfterSeconds;

            // Never above the value the game itself set - otherwise on a 60 Hz screen this patch would
            // want to RAISE the frame rate.
            int want = idle ? IdleFps : (savedTarget > 0 ? Mathf.Min(savedTarget, ActiveFps) : ActiveFps);
            // Also reapply when someone else changed the value underneath us (RimWorld sets it at startup
            // and on resolution change, another mod can do so at any time). Otherwise we would have a lock
            // that silently gets lost while still counting as set.
            if (want == appliedFps && Application.targetFrameRate == appliedFps) return;
            appliedFps = want;
            // ============ vSync is NO LONGER touched ============
            // This used to say: "with vSync on, Unity ignores targetFrameRate, so we turn it off while we
            // hold". The first half is true; the second was a mistake with visible consequences.
            //
            // Observed repeatedly: the screen flickered. The measurement line
            // from the running game read: "76 fps (target 144, vSync 1)". So vSync was at 1 WHILE
            // we were holding - something had overwritten our zero. The test one line above only checks
            // targetFrameRate, not vSync: so we wrote the zero again on every frame in which the value
            // was back at 1. Toggling vSync on and off per frame is exactly what a screen shows as
            // flickering - Unity rebuilds the frame timing each time.
            //
            // So the line never achieved what it was there for (the 76 fps prove it: that is half the
            // refresh rate, i.e. vSync), and flickered in the process. A lever that doesn't work
            // and does harm instead goes away.
            //
            // What this costs, honestly: with vSync on the throttling has no effect -
            // Unity ignores targetFrameRate then, and no mod can change that without causing exactly
            // this damage. In that case the fan stays up, and the right lever is the other one:
            // less work per frame instead of fewer frames.
            Application.targetFrameRate = want;
            // The frame-rate measurement line was dropped: a load measurement showed that the developer
            // log window itself accounted for 530 of 537 ms/s of all
            // window cost. The throttling below is unchanged - only its logging is gone.
        }

        private static void Release()
        {
            if (!held) return;
            held = false;
            appliedFps = -1;
            Application.targetFrameRate = savedTarget;
            // NO more resetting of vSync: we no longer touch the value, so it is not ours either.
            // Writing it here would mean resetting a setting that the player or the driver may have
            // changed in the meantime - and on opening/closing a window that was exactly the second
            // flicker moment.
        }

        // Two conditions, and the second is the important one: ONLY when the game is standing still
        // because of a window anyway. A window of ours that does not pause the game must not touch the
        // frame rate of the running map - something is moving there that has to stay smooth.
        private static bool ShouldHold()
        {
            WindowStack stack = Find.WindowStack;
            if (stack == null || stack.Count == 0) return false;
            if (!stack.WindowsForcePause) return false;
            for (int i = 0; i < stack.Count; i++)
            {
                Window w = stack[i];
                // Via the assembly instead of a list of window types: an enumeration would silently be
                // incomplete with the next new dialog (same reasoning as in
                // CombatReadiness.AnyModWindowOpen). stack.Windows would be wrong here - it is an
                // AsReadOnly() and allocates on EVERY call; Count + indexer do not.
                if (w != null && w.GetType().Assembly == OwnAssembly) return true;
            }
            return false;
        }
    }
}
