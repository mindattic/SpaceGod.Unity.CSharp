using UnityEngine;
using UnityEngine.InputSystem;

namespace Unity.SpaceGod.Input
{
    /// <summary>
    /// One frame of player input, polled from the Input System devices. Keeping all key reads in
    /// one place makes the bindings easy to find and keeps the camera and app logic device-free.
    /// </summary>
    public sealed class InputState
    {
        /// <summary>x = strafe, y = rise, z = forward; each in [-1, 1].</summary>
        public Vector3 Move;
        public Vector2 LookDelta;
        public bool LookHeld;

        /// <summary>Raw mouse delta, unconditional (unlike <see cref="LookDelta"/>, which only reports while
        /// the right button is held for camera orbit) — piloting reads this for always-on heading control.</summary>
        public Vector2 MouseDelta;
        public float Scroll;
        public bool Boost;
        public bool Slow;

        /// <summary>Held while piloting: engages the cruise drive (Elite-style) for fast open-space
        /// travel. See <see cref="Unity.SpaceGod.Core.PilotCruiseState"/>.</summary>
        public bool Cruise;

        /// <summary>While piloting a capital ship (U.SPC-T499): W rings its engine telegraph one step ahead, S one step astern (0: neither).</summary>
        public int ThrottleStep;

        /// <summary>While piloting: roll, -1 (Z, anticlockwise) .. 1 (X, clockwise), seen from behind.</summary>
        public float Roll;

        /// <summary>While piloting: switches flight assist on or off (shares F with <see cref="Focus"/>,
        /// which is gated off while piloting, so the two never fire together).</summary>
        public bool ToggleFlightAssist;

        /// <summary>Held, not click-edged: while piloting, fires the ship's forward-mount weapons.
        /// Left click still selects normally outside piloting, via the click-edged <see cref="SelectClicked"/>
        /// below (gated off while piloting, so holding the trigger down and releasing it never also selects).</summary>
        public bool Fire;

        /// <summary>While piloting, in a fight (U.SPC-T271): the EMP trigger (middle mouse, held), and a mine dropped
        /// astern (Ctrl, on the press).</summary>
        public bool FireEmp, DropMine;

        public bool SelectClicked;
        public bool SelectDoubleClicked;
        public Vector2 MousePosition;

        public bool Focus;
        public bool Escape;
        public bool ToggleScale;
        public bool TimeFaster;
        public bool TimeSlower;
        public bool TogglePause;
        public bool ResetTime;
        public bool ToggleOrbits;
        public bool ToggleLabels;
        public bool ToggleHelp;
        /// <summary>U: the lighter toggle, the HUD's panels and readouts off while the names and markers on the view stay.</summary>
        public bool ToggleHud;

        /// <summary>F11 (U.SPC-T496): clears the screen of all its UI but the reticle in use, and brings it all back.</summary>
        public bool ClearScreen;
        public bool ToggleMarket;
        public bool ToggleCargo;
        public bool ToggleOutfit;

        /// <summary>While piloting: switches silent running on or off (F3; see <see cref="Unity.SpaceGod.Core.Ship.SilentRunning"/>).</summary>
        public bool ToggleSilent;

        /// <summary>The bounty board of the port in view (F4): who is wanted there, for how much, and where last seen.</summary>
        public bool ToggleBounties;

        /// <summary>The contracts board of the port in view (F7): jobs to take, and the player's own (U.SPC-T101).</summary>
        public bool ToggleContracts;

        /// <summary>The player's own fleet and property (F10; F9 is the model viewer's): owned ships, captains, standing orders, the books.</summary>
        public bool ToggleFleet;

        /// <summary>The trade desk (backslash, U.SPC-T426): runs, brokers, trade news, charters, insurance, warehouses, the back door.</summary>
        public bool ToggleTrade;

        /// <summary>[ (left bracket): the galaxy map (U.SPC-T103; on F11 until U.SPC-T496 gave that to the clear screen).</summary>
        public bool ToggleGalaxyMap;

        /// <summary>F12 (U.SPC-T427): hails the polity of the ship locked, selected or hailing you, and opens the envoy screen to treat with it.</summary>
        public bool HailPolity;

        /// <summary>The news feed: the full log of what has happened, filterable (X).</summary>
        public bool ToggleNews;

        /// <summary>Dev/sandbox panel: spawn a ship of any class at the camera and pilot it.</summary>
        public bool ToggleSpawn;
        public bool WatchBattle;
        public bool ToggleLanes;
        public bool ToggleSatellites;

        /// <summary>U.SPC-T595: Shift+K or numpad * moves the system in view on to its next skybox (+1); Shift+Alt+K or
        /// numpad / back to the one before (-1).</summary>
        public int CycleSky;
        public bool ToggleReorient;
        public bool CycleShips;
        public bool CycleBackward;
        public bool Home;
        public int NumberKey = -1;

        /// <summary>Takes or releases direct control of the selected/focused ship.</summary>
        public bool Pilot;

        /// <summary>Take charge (U.SPC-T422): ' takes the next seat in the fight in view (a ship's helm or a launched
        /// fighter's cockpit), ; the one before (0: neither).</summary>
        public int CycleSeat;

        /// <summary>Take charge (U.SPC-T422): / mans the next gun on the hull flown or selected, and past its last gun
        /// goes back up to the helm.</summary>
        public bool ManGun;

        /// <summary>At a gun mount (U.SPC-T445): Backspace moves up from the gun to the helm of the hull it is mounted on
        /// (in the strategy view Backspace is Home).</summary>
        public bool MoveToHost;

        /// <summary>F9 (U.SPC-T446): the take-charge picker, every seat on the thing in hand and around the camera to click into.</summary>
        public bool TogglePicker;

        /// <summary>While piloting in a fight (U.SPC-T488): Insert retunes the hull's phaser arrays, a narrow lance for hulls or a wide sweep for fighters and missiles.</summary>
        public bool TunePhasers;

        /// <summary>B at the controls (in the strategy view B watches a battle): the boarding command panel, for a fight aboard the piloted hull or one its marines are boarding (U.SPC-T468).</summary>
        public bool ToggleBoarding;
        /// <summary>At a big ship's helm (U.SPC-T491): Shift+Insert orders the crew to run hot, keeping the guns and drive at full however
        /// hot the hull gets, or to throttle down again.</summary>
        public bool ToggleRunHot;

        /// <summary>While piloting: locks (or clears) the camera's tracking onto the hovered/selected
        /// ship. Shares its key with <see cref="ResetTime"/>, which is gated off while piloting so the
        /// two never fire together.</summary>
        public bool LockTarget;

        /// <summary>While piloting, in range of one of this system's jump gates: activates it.</summary>
        public bool ActivateGate;

        /// <summary>While piloting a hull with its own FTL drive (U.SPC-T417): spools the drive for what the nose is on (Enter), or calls a charge off.</summary>
        public bool EngageFtl;

        /// <summary>While piloting a hull with its own FTL drive (U.SPC-T420): End engages the FTL autopilot for the plotted
        /// course (plotting one for the star the nose is on if there is none), or lets go of it.</summary>
        public bool ToggleFtlAutopilot;

        /// <summary>While piloting a hull with its own FTL drive (U.SPC-T420): Page Down plots a course to the next star or
        /// beacon out, nearest first, Page Up the one before (0: neither).</summary>
        public int CycleFtlTarget;

        /// <summary>While piloting (U.SPC-T420): Delete clears the plotted FTL course.</summary>
        public bool ClearFtlCourse;

        /// <summary>At a capital ship's helm (U.SPC-T502): Shift+Delete orders the helm assist to roll the hull's weak shield
        /// patches away from the heaviest fire, or lets the roll go back to the helmsman.</summary>
        public bool ToggleRollPatches;

        /// <summary>F5 saves to the quicksave slot, F8 loads it, F6 opens the save slots.</summary>
        public bool QuickSave, QuickLoad, ToggleSaves;

        Vector2 pressPosition;
        float lastClickTime = -10f;
        Vector2 lastClickPosition;
        bool rightDragged;
        Vector2 rightPressPosition;

        /// <summary>True on the frame the right button was released without dragging (a "right click").</summary>
        public bool RightClicked;

        /// <summary>Reads this frame's input; with <paramref name="suspended"/> (another screen has the
        /// keyboard and mouse, such as the model viewer) everything reads as idle instead.</summary>
        public void Poll(bool pointerOverUi, bool suspended = false)
        {
            var kb = Keyboard.current;
            var mouse = Mouse.current;
            Reset();
            if (suspended || kb == null || mouse == null) return;

            Move = new Vector3(
                Axis(kb.dKey, kb.aKey),
                Axis(kb.eKey, kb.qKey) + Axis(kb.rightBracketKey, kb.cKey),
                Axis(kb.wKey, kb.sKey));
            Move.y = Mathf.Clamp(Move.y, -1, 1);
            Boost = kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed;
            Slow = kb.leftCtrlKey.isPressed || kb.rightCtrlKey.isPressed;
            Cruise = kb.jKey.isPressed;
            ThrottleStep = kb.wKey.wasPressedThisFrame ? 1 : kb.sKey.wasPressedThisFrame ? -1 : 0;
            Roll = Axis(kb.xKey, kb.zKey);

            MousePosition = mouse.position.ReadValue();
            MouseDelta = mouse.delta.ReadValue();
            Fire = mouse.leftButton.isPressed && !pointerOverUi;
            FireEmp = mouse.middleButton.isPressed && !pointerOverUi;
            DropMine = kb.leftCtrlKey.wasPressedThisFrame || kb.rightCtrlKey.wasPressedThisFrame;
            LookHeld = mouse.rightButton.isPressed;
            LookDelta = LookHeld ? MouseDelta : Vector2.zero;
            if (mouse.rightButton.wasPressedThisFrame)
            {
                rightPressPosition = MousePosition;
                rightDragged = false;
            }
            if (LookHeld && (MousePosition - rightPressPosition).sqrMagnitude > 16) rightDragged = true;
            if (mouse.rightButton.isPressed && mouse.delta.ReadValue().sqrMagnitude > 4) rightDragged = true;
            RightClicked = mouse.rightButton.wasReleasedThisFrame && !rightDragged;

            if (!pointerOverUi)
            {
                float wheel = mouse.scroll.ReadValue().y;
                if (Mathf.Abs(wheel) > 0.01f)
                {
                    // Standard mice report multiples of 120 per notch; trackpads and high-resolution wheels
                    // already report small per-notch values directly, so scale each convention on its own
                    // rather than dividing both by 120 and snapping the underflowed result to a full notch.
                    float normalized = Mathf.Abs(wheel) < 2f ? wheel : wheel / 120f;
                    Scroll = Mathf.Clamp(normalized, -3f, 3f);
                }
                else
                {
                    Scroll = 0f;
                }
            }

            if (mouse.leftButton.wasPressedThisFrame) pressPosition = MousePosition;
            if (mouse.leftButton.wasReleasedThisFrame && !pointerOverUi && (MousePosition - pressPosition).sqrMagnitude < 36)
            {
                SelectClicked = true;
                float now = Time.unscaledTime;
                SelectDoubleClicked = now - lastClickTime < 0.35f && (MousePosition - lastClickPosition).sqrMagnitude < 100;
                lastClickTime = SelectDoubleClicked ? -10f : now;
                lastClickPosition = MousePosition;
            }

            Focus = kb.fKey.wasPressedThisFrame;
            ToggleFlightAssist = kb.fKey.wasPressedThisFrame;
            Escape = kb.escapeKey.wasPressedThisFrame;
            ToggleScale = kb.gKey.wasPressedThisFrame || kb.backquoteKey.wasPressedThisFrame;
            TimeFaster = kb.periodKey.wasPressedThisFrame || kb.equalsKey.wasPressedThisFrame || kb.numpadPlusKey.wasPressedThisFrame;
            TimeSlower = kb.commaKey.wasPressedThisFrame || kb.minusKey.wasPressedThisFrame || kb.numpadMinusKey.wasPressedThisFrame;
            TogglePause = kb.spaceKey.wasPressedThisFrame || kb.pauseKey.wasPressedThisFrame;
            ResetTime = kb.tKey.wasPressedThisFrame;
            LockTarget = kb.tKey.wasPressedThisFrame;
            ToggleOrbits = kb.oKey.wasPressedThisFrame;
            ToggleLabels = kb.lKey.wasPressedThisFrame;
            ToggleHelp = kb.hKey.wasPressedThisFrame || kb.f1Key.wasPressedThisFrame;
            ToggleHud = kb.uKey.wasPressedThisFrame;
            ToggleMarket = kb.mKey.wasPressedThisFrame;
            ToggleCargo = kb.iKey.wasPressedThisFrame;
            ToggleOutfit = kb.f2Key.wasPressedThisFrame;
            ToggleSilent = kb.f3Key.wasPressedThisFrame;
            ToggleBounties = kb.f4Key.wasPressedThisFrame;
            ToggleContracts = kb.f7Key.wasPressedThisFrame;
            ToggleFleet = kb.f10Key.wasPressedThisFrame;
            ToggleTrade = kb.backslashKey.wasPressedThisFrame;
            ToggleGalaxyMap = kb.leftBracketKey.wasPressedThisFrame;
            ClearScreen = kb.f11Key.wasPressedThisFrame;
            HailPolity = kb.f12Key.wasPressedThisFrame;
            ToggleNews = kb.xKey.wasPressedThisFrame;
            ToggleSpawn = kb.vKey.wasPressedThisFrame;
            WatchBattle = kb.bKey.wasPressedThisFrame;
            ToggleLanes = kb.nKey.wasPressedThisFrame;
            bool skyShift = kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed;
            bool skyAlt = kb.leftAltKey.isPressed || kb.rightAltKey.isPressed;
            ToggleSatellites = kb.kKey.wasPressedThisFrame && !skyShift;
            CycleSky = (kb.kKey.wasPressedThisFrame && skyShift && skyAlt) || kb.numpadDivideKey.wasPressedThisFrame ? -1
                : (kb.kKey.wasPressedThisFrame && skyShift) || kb.numpadMultiplyKey.wasPressedThisFrame ? 1 : 0;
            ToggleReorient = kb.rKey.wasPressedThisFrame;
            CycleShips = kb.tabKey.wasPressedThisFrame;
            CycleBackward = CycleShips && Boost;
            Home = kb.homeKey.wasPressedThisFrame || kb.backspaceKey.wasPressedThisFrame;
            Pilot = kb.pKey.wasPressedThisFrame;
            CycleSeat = kb.quoteKey.wasPressedThisFrame ? 1 : kb.semicolonKey.wasPressedThisFrame ? -1 : 0;
            ManGun = kb.slashKey.wasPressedThisFrame;
            MoveToHost = kb.backspaceKey.wasPressedThisFrame;
            TogglePicker = kb.f9Key.wasPressedThisFrame;
            bool shifted = kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed;
            TunePhasers = kb.insertKey.wasPressedThisFrame && !shifted;
            ToggleBoarding = kb.bKey.wasPressedThisFrame;
            ToggleRunHot = kb.insertKey.wasPressedThisFrame && shifted;
            ActivateGate = kb.yKey.wasPressedThisFrame;
            EngageFtl = kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame;
            ToggleFtlAutopilot = kb.endKey.wasPressedThisFrame;
            CycleFtlTarget = kb.pageDownKey.wasPressedThisFrame ? 1 : kb.pageUpKey.wasPressedThisFrame ? -1 : 0;
            ClearFtlCourse = kb.deleteKey.wasPressedThisFrame && !shifted;
            ToggleRollPatches = kb.deleteKey.wasPressedThisFrame && shifted;
            QuickSave = kb.f5Key.wasPressedThisFrame;
            QuickLoad = kb.f8Key.wasPressedThisFrame;
            ToggleSaves = kb.f6Key.wasPressedThisFrame;

            Key[] digits = { Key.Digit0, Key.Digit1, Key.Digit2, Key.Digit3, Key.Digit4, Key.Digit5, Key.Digit6, Key.Digit7, Key.Digit8, Key.Digit9 };
            for (int i = 0; i < digits.Length; i++)
                if (kb[digits[i]].wasPressedThisFrame) NumberKey = i;
        }

        /// <summary>Drops this frame's right-button look (a full screen of its own has the mouse): no look, no right-click.</summary>
        public void ReleaseLook()
        {
            LookHeld = false;
            LookDelta = Vector2.zero;
            RightClicked = false;
        }

        void Reset()
        {
            Move = Vector3.zero;
            LookDelta = Vector2.zero;
            LookHeld = false;
            MouseDelta = Vector2.zero;
            Scroll = 0;
            Roll = 0;
            ToggleFlightAssist = false;
            Boost = Slow = Fire = Cruise = FireEmp = DropMine = false;
            SelectClicked = SelectDoubleClicked = RightClicked = false;
            Focus = Escape = ToggleScale = TimeFaster = TimeSlower = TogglePause = ResetTime = false;
            ToggleOrbits = ToggleLabels = ToggleHelp = ToggleHud = ClearScreen = ToggleMarket = ToggleCargo = ToggleOutfit = ToggleSilent = ToggleBounties = ToggleContracts = ToggleFleet = ToggleTrade = ToggleGalaxyMap = ToggleNews = ToggleSpawn = WatchBattle = ToggleLanes = ToggleSatellites = ToggleReorient = CycleShips = CycleBackward = Home = Pilot = LockTarget = ActivateGate = EngageFtl = false;
            QuickSave = QuickLoad = ToggleSaves = false;
            ToggleFtlAutopilot = ClearFtlCourse = false;
            CycleFtlTarget = 0;
            CycleSeat = 0;
            CycleSky = 0;
            ThrottleStep = 0;
            ManGun = false;
            MoveToHost = false;
            TogglePicker = false;
            TunePhasers = false;
            ToggleBoarding = false;
            ToggleRunHot = false;
            ToggleRollPatches = false;
            NumberKey = -1;
        }

        static float Axis(UnityEngine.InputSystem.Controls.KeyControl positive, UnityEngine.InputSystem.Controls.KeyControl negative) =>
            (positive.isPressed ? 1f : 0f) - (negative.isPressed ? 1f : 0f);
    }
}
