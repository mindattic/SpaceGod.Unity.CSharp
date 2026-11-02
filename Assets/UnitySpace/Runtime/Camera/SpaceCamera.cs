using System;
using Unity.SpaceGod.Core;
using Unity.SpaceGod.Input;
using UnityEngine;

namespace Unity.SpaceGod.Cameras
{
    /// <summary>What the camera needs to know about the world.</summary>
    public interface ICameraWorld
    {
        BodyCatalog Catalog { get; }
        ScaleModel Scale { get; }
        DVec3 BodyScene(int body);
        DVec3 TargetScene(Target target);

        /// <summary>A target's framing radius in scene units (body radius, or half a ship's length).</summary>
        double TargetRadius(Target target);

        /// <summary>Distance from a scene point to the nearest surface (body or ship hull), scene units.</summary>
        double NearestSurface(DVec3 scenePosition);

        /// <summary>A target's own facing, scene (Unity) axes; identity when it has none.</summary>
        Quaternion TargetRotation(Target target);
    }

    /// <summary>
    /// Homeworld 3 style space camera.
    ///
    ///  - Free: swim like Quake. WASD along the view, Q/E (Space/C) up and down, hold the right
    ///    mouse button to look. Speed scales with distance to the nearest surface, so the same keys
    ///    cross an AU in deep space and creep along a hull up close.
    ///  - Focus (F): fly to the selected body or ship and orbit it; right-drag orbits, the wheel
    ///    zooms, and the camera rides along with the target as it moves. Any movement key drops
    ///    straight back into free swim from where you are.
    ///
    /// Free-camera position is stored as a true (km) offset from a reference body, the body whose
    /// sphere of influence you are in, so the camera co-moves with whatever you are near under any
    /// time warp, and keeps its place relative to it when the scale is toggled.
    ///
    /// "Up" is a frame of its own (<see cref="UpFrame"/>): the ecliptic in Sol, but any plane can be
    /// made level (Alpha Centauri's A-B orbit lies steeply across the ecliptic). Yaw, pitch and the
    /// focus orbit are measured in that frame; <see cref="Level"/> swings to a new one smoothly.
    /// </summary>
    [SettingsSource]
    public sealed class SpaceCamera
    {
        public enum CameraMode
        {
            Free,
            Travel,
            Focus,
        }

        public CameraMode Mode { get; private set; } = CameraMode.Free;
        public DVec3 ScenePosition { get; private set; }
        public Quaternion Rotation { get; private set; } = Quaternion.identity;
        public Target FocusTarget { get; private set; } = Target.None;
        public int FrameBody => frame;

        /// <summary>Which way is up: Unity's +Y turned onto the plane the view keeps level.</summary>
        public Quaternion UpFrame { get; private set; } = Quaternion.identity;

        /// <summary>True while swinging round to a new level.</summary>
        public bool Levelling => levelT < 1f;

        /// <summary>Current swim speed in scene units per second (for the HUD).</summary>
        public double CurrentSpeed { get; private set; }

        public double SpeedMultiplier { get; private set; } = 1.0;

        /// <summary>True while piloting: <see cref="Focus"/> rides a fixed distance behind the target and
        /// matches its own facing instead of taking right-drag orbit input, which steers the ship instead.</summary>
        public bool PilotLocked { get; private set; }

        /// <summary>While piloting: a ship the camera leans toward keeping framed, without taking the
        /// piloted ship's own facing away from the player (see the <see cref="PilotLocked"/> branch of
        /// <see cref="UpdateFocus"/>). <see cref="Target.None"/> when nothing is locked.</summary>
        public Target PilotLockTarget { get; set; } = Target.None;

        /// <summary>While piloting: where the view looks when it is not simply the hull's own facing (U.SPC-T269)
        /// -- along a light fighter's crosshair, or swung about a capital ship by the mouse. Null follows the hull.</summary>
        public Quaternion? PilotView { get; set; }

        /// <summary>While piloting: where the eye itself sits when it is not a chase behind the hull (U.SPC-T445) -- at a
        /// manned gun mount, looking down its barrel along <see cref="PilotView"/>. Read each frame, so the eye rides the
        /// hull as it moves; null keeps the chase. Cleared whenever a new chase begins or the focus is let go.</summary>
        public Func<DVec3> PilotEye { get; set; }

        /// <summary>How fast the view catches up to <see cref="PilotView"/>: quick, since in free aim the
        /// crosshair is the middle of the screen and must stay under the mouse.</summary>
        const float PilotViewResponseHz = 30f;

        /// <summary>How far above the line of sight a free-aim chase sits, in chase distances, so the
        /// hull flies below the crosshair instead of covering it.</summary>
        const double PilotViewLift = 0.22;

        const float TravelSeconds = 1.6f;
        const float LevelSeconds = 1.4f;
        /// <summary>Mouse look, in degrees a count at the default sensitivity; the free, focus and pilot views alike.</summary>
        const float BaseLookSensitivity = 0.12f;

        /// <summary>Controls (U.SPC-T431): how far the mouse turns the view, as a multiple of the usual.</summary>
        public static readonly RangeSetting LookSensitivitySetting = new RangeSetting("controls.lookSensitivity", SettingGroups.Controls,
            "Mouse look sensitivity", 1, 0.25, 4, 0.05,
            "How far the view (and a fighter's aim) turns for a mouse movement.",
            v => v.ToString("0.00") + "x", "mouse camera speed aim");

        /// <summary>Controls (U.SPC-T431): mouse forward looks down, as in a flight stick.</summary>
        public static readonly ToggleSetting InvertLook = new ToggleSetting("controls.invertLook", SettingGroups.Controls,
            "Invert vertical look", false, "Pushing the mouse forward looks (and pitches) down instead of up.", "mouse y pitch flight");

        /// <summary>Degrees of turn per mouse count, as the player has set it.</summary>
        public static float LookSensitivity => BaseLookSensitivity * (float)LookSensitivitySetting.Value;

        /// <summary>A mouse movement's up-down part, the way the player wants it read.</summary>
        public static float LookY(float dy) => InvertLook.Value ? -dy : dy;
        const double MinSpeedMultiplier = 1.0 / 64, MaxSpeedMultiplier = 64;

        /// <summary>How fast the pilot chase camera's rotation catches up to the hull's own facing
        /// (and, with a lock, the blended look-at-target facing) -- high enough to feel tight behind
        /// the ship, low enough that sharp turns read as a chase rather than a rigid weld.</summary>
        const float PilotCamResponseHz = 12f;

        /// <summary>How far the camera leans from the ship's own facing toward looking at a locked
        /// target (0 = ignores the lock, 1 = looks straight at it regardless of the ship's facing).</summary>
        const float PilotLockTrackStrength = 0.6f;

        readonly ICameraWorld world;

        int frame;
        DVec3 trueOffset;
        float yaw, pitch;
        Vector3 velocity;

        float orbitYaw, orbitPitch;
        double zoom = 4, zoomTarget = 4;

        DVec3 travelStartOffset;
        Quaternion travelStartRotation;
        DVec3 travelEndDirection;
        double travelEndZoom;
        float travelT;

        // The rotation the view had before levelling, relative to where it is now, eased away.
        Quaternion levelOffset = Quaternion.identity;
        float levelT = 1f;

        public SpaceCamera(ICameraWorld world)
        {
            this.world = world;
        }

        Vector3 Up => UpFrame * Vector3.up;

        /// <summary>A world direction expressed in the level frame.</summary>
        Vector3 ToLocal(Vector3 v) => Quaternion.Inverse(UpFrame) * v;

        // ---------------------------------------------------------------- commands

        /// <summary>
        /// Swings the view round, over a second and a half, so that the plane with normal
        /// <paramref name="planeNormal"/> (Unity axes) is level: the camera stays where it is and keeps
        /// looking the same way as nearly as it can, losing only its roll against the new horizon.
        /// </summary>
        public void Level(Vector3 planeNormal)
        {
            if (planeNormal.sqrMagnitude < 1e-12f) planeNormal = Vector3.up;
            var before = Rotation;
            UpFrame = Quaternion.FromToRotation(Vector3.up, planeNormal.normalized);
            RederiveAngles();
            Rotation = ComposedRotation();
            levelOffset = before * Quaternion.Inverse(Rotation);
            levelT = 0f;
        }

        /// <summary>Takes a new level frame at once (switching systems, say), keeping the view as it is.</summary>
        public void SetUpFrame(Quaternion frame)
        {
            UpFrame = frame;
            levelT = 1f;
            RederiveAngles();
        }

        /// <summary>Yaw/pitch (free) and orbit angles (focus) from the current view, in the current frame.</summary>
        void RederiveAngles()
        {
            var forward = ToLocal(Rotation * Vector3.forward);
            yaw = Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
            pitch = Mathf.Clamp(-Mathf.Asin(Mathf.Clamp(forward.y, -1, 1)) * Mathf.Rad2Deg, -89f, 89f);
            if (Mode == CameraMode.Travel)
            {
                // Mid-flight: re-express where the flight ends, not where the camera happens to be.
                var look = -ToLocal(ToVector(travelEndDirection));
                orbitYaw = Mathf.Atan2(look.x, look.z) * Mathf.Rad2Deg;
                orbitPitch = Mathf.Clamp(-Mathf.Asin(Mathf.Clamp(look.y, -1, 1)) * Mathf.Rad2Deg, -88f, 88f);
            }
            else if (Mode == CameraMode.Focus && !FocusTarget.IsNone)
            {
                var offset = ScenePosition - world.TargetScene(FocusTarget);
                if (offset.LengthSquared > 0)
                {
                    var look = -ToLocal(ToVector(offset.Normalized));
                    orbitYaw = Mathf.Atan2(look.x, look.z) * Mathf.Rad2Deg;
                    orbitPitch = Mathf.Clamp(-Mathf.Asin(Mathf.Clamp(look.y, -1, 1)) * Mathf.Rad2Deg, -88f, 88f);
                }
            }
        }

        Quaternion ComposedRotation() =>
            Mode == CameraMode.Focus ? UpFrame * Quaternion.Euler(orbitPitch, orbitYaw, 0) : UpFrame * Quaternion.Euler(pitch, yaw, 0);

        /// <summary>Places the free camera at a scene position looking along yaw/pitch (world axes).</summary>
        public void PlaceFree(DVec3 scenePosition, float yawDeg, float pitchDeg)
        {
            Mode = CameraMode.Free;
            FocusTarget = Target.None;
            SetRotation(Quaternion.Euler(Mathf.Clamp(pitchDeg, -89f, 89f), yawDeg, 0));
            velocity = Vector3.zero;
            SetScenePosition(scenePosition);
        }

        /// <summary>Puts the free camera at a scene position with a rotation (driven from outside, as by star travel).</summary>
        public void SetPose(DVec3 scenePosition, Quaternion rotation)
        {
            Mode = CameraMode.Free;
            FocusTarget = Target.None;
            SetRotation(rotation);
            velocity = Vector3.zero;
            SetScenePosition(scenePosition);
        }

        public void SetRotation(Quaternion rotation)
        {
            Rotation = rotation;
            var forward = ToLocal(rotation * Vector3.forward);
            yaw = Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
            pitch = Mathf.Clamp(-Mathf.Asin(Mathf.Clamp(forward.y, -1, 1)) * Mathf.Rad2Deg, -89f, 89f);
        }

        /// <summary>Flies to <paramref name="target"/> and enters focus. <paramref name="radii"/> overrides the framing distance.</summary>
        public void FocusOn(Target target, double radii = double.NaN, float? pitchOverride = null)
        {
            if (target.IsNone) return;
            var targetScene = world.TargetScene(target);
            double r = Math.Max(world.TargetRadius(target), 1e-9);
            var offset = ScenePosition - targetScene;
            double distance = offset.Length;

            double framing = !double.IsNaN(radii) ? radii : DefaultFraming(target);
            double currentRadii = distance / r;
            if (double.IsNaN(radii) && Mode != CameraMode.Free && FocusTarget == target) framing = zoomTarget;
            else if (double.IsNaN(radii) && currentRadii > MinZoom(target) * 1.05 && currentRadii < framing) framing = currentRadii;

            // Work in the level frame: elevation is measured from its horizon.
            var dir = distance > 0 ? ToLocal(ToVector(offset / distance)) : new Vector3(0, 0.3f, -1).normalized;
            // Approach from the side we are already on, but never from straight over a pole.
            var flat = new Vector3(dir.x, 0, dir.z);
            float elevation = Mathf.Asin(Mathf.Clamp(dir.y, -1, 1)) * Mathf.Rad2Deg;
            elevation = pitchOverride ?? Mathf.Clamp(elevation, -55f, 55f);
            if (flat.sqrMagnitude < 1e-8f) flat = Vector3.back;
            flat.Normalize();
            float e = elevation * Mathf.Deg2Rad;
            var endLocal = new Vector3(flat.x * Mathf.Cos(e), Mathf.Sin(e), flat.z * Mathf.Cos(e));
            var endWorld = UpFrame * endLocal;

            travelStartOffset = distance > 0 ? offset : new DVec3(endWorld.x, endWorld.y, endWorld.z) * r * framing * 3;
            travelStartRotation = Rotation;
            travelEndDirection = new DVec3(endWorld.x, endWorld.y, endWorld.z);
            travelEndZoom = framing;
            travelT = 0;

            // The orbit angles that reproduce endDir: offset = Euler(pitch, yaw) * back, in the level frame.
            var look = -endLocal;
            orbitYaw = Mathf.Atan2(look.x, look.z) * Mathf.Rad2Deg;
            orbitPitch = -Mathf.Asin(Mathf.Clamp(look.y, -1, 1)) * Mathf.Rad2Deg;
            zoom = zoomTarget = framing;

            FocusTarget = target;
            Mode = CameraMode.Travel;
            velocity = Vector3.zero;
        }

        public void ExitFocus()
        {
            if (Mode == CameraMode.Free) return;
            var rotation = Rotation;
            Mode = CameraMode.Free;
            FocusTarget = Target.None;
            PilotLocked = false;
            PilotView = null;
            PilotEye = null;
            SetRotation(rotation);
            velocity = Vector3.zero;
            SetScenePosition(ScenePosition);
        }

        /// <summary>Settles into a close chase a fixed distance behind a piloted ship: its own facing
        /// drives the view, so right-drag is free for flight input instead of orbiting.</summary>
        public void EnterPilotChase(Target target, double radii)
        {
            FocusOn(target, radii);
            PilotLocked = true;
            PilotEye = null;
        }

        public static double DefaultFraming(Target target)
        {
            return target.IsShip || target.IsUnit ? 5.0 : 3.4;
        }

        static double MinZoom(Target target) => target.IsShip || target.IsUnit ? 1.6 : 1.12;

        // ---------------------------------------------------------------- per frame

        public void Update(float dt, InputState input)
        {
            bool moving = input.Move.sqrMagnitude > 0.01f;
            if (Mode != CameraMode.Free && moving && !PilotLocked) ExitFocus();

            switch (Mode)
            {
                case CameraMode.Travel:
                    UpdateTravel(dt);
                    break;
                case CameraMode.Focus:
                    UpdateFocus(dt, input);
                    break;
                default:
                    UpdateFree(dt, input);
                    break;
            }

            // Levelling: the old orientation's difference from the new one eases away.
            if (levelT < 1f)
            {
                levelT = Mathf.Min(1f, levelT + dt / LevelSeconds);
                Rotation = Quaternion.Slerp(levelOffset, Quaternion.identity, SmootherStep(levelT)) * Rotation;
            }
        }

        void UpdateFree(float dt, InputState input)
        {
            if (input.LookHeld)
            {
                yaw += input.LookDelta.x * LookSensitivity;
                pitch = Mathf.Clamp(pitch - LookY(input.LookDelta.y) * LookSensitivity, -89f, 89f);
            }
            Rotation = UpFrame * Quaternion.Euler(pitch, yaw, 0);

            if (Math.Abs(input.Scroll) > 0)
                SpeedMultiplier = Math.Max(MinSpeedMultiplier, Math.Min(MaxSpeedMultiplier, SpeedMultiplier * Math.Pow(1.3, input.Scroll)));

            // Re-derive the position from the frame (the frame body may have moved or the scale changed).
            ScenePosition = FramePosition();

            double surface = Math.Max(world.NearestSurface(ScenePosition), 1e-6);
            double speed = surface * 0.9 * SpeedMultiplier * (input.Boost ? 6 : 1) * (input.Slow ? 0.15 : 1);
            CurrentSpeed = speed;

            var desired = Rotation * Vector3.ClampMagnitude(input.Move, 1f) * (float)speed;
            float response = 1f - Mathf.Exp(-dt * 7f);
            velocity = Vector3.Lerp(velocity, desired, response);
            if (input.Move.sqrMagnitude < 0.01f && velocity.magnitude < speed * 0.002) velocity = Vector3.zero;

            var delta = new DVec3(velocity.x, velocity.y, velocity.z) * dt;
            if (delta.LengthSquared > 0) ApplySceneDelta(delta);

            ScenePosition = FramePosition();
            if (PushOutOfBodies()) velocity *= 0.3f;
            UpdateFrame();
            ScenePosition = FramePosition();
        }

        void UpdateFocus(float dt, InputState input)
        {
            if (PilotLocked)
            {
                var shipRotation = world.TargetRotation(FocusTarget);
                var wantRotation = PilotView ?? shipRotation;
                if (PilotView == null && !PilotLockTarget.IsNone)
                {
                    var toTarget = world.TargetScene(PilotLockTarget) - world.TargetScene(FocusTarget);
                    if (toTarget.LengthSquared > 1e-12)
                    {
                        var lookAt = Quaternion.LookRotation(ToVector(toTarget.Normalized), shipRotation * Vector3.up);
                        wantRotation = Quaternion.Slerp(shipRotation, lookAt, PilotLockTrackStrength);
                    }
                }
                // Eases toward the hull's facing (and any lock-on blend) rather than welding to it
                // instantly, so sharp turns read as a chase rather than a rigid 1:1 attachment.
                Rotation = Quaternion.Slerp(Rotation, wantRotation, 1f - Mathf.Exp(-dt * (PilotView != null ? PilotViewResponseHz : PilotCamResponseHz)));
                if (Math.Abs(input.Scroll) > 0)
                    zoomTarget = Math.Max(MinZoom(FocusTarget), Math.Min(20, zoomTarget * Math.Pow(0.82, input.Scroll)));
                zoom = Math.Exp(Math.Log(zoom) + (Math.Log(zoomTarget) - Math.Log(zoom)) * (1 - Math.Exp(-dt * 10)));
                var chaseBack = Rotation * Vector3.back;
                double chaseR = world.TargetRadius(FocusTarget);
                ScenePosition = world.TargetScene(FocusTarget) + new DVec3(chaseBack.x, chaseBack.y, chaseBack.z) * (zoom * chaseR);
                if (PilotView != null)
                {
                    var lift = Rotation * Vector3.up;
                    ScenePosition += new DVec3(lift.x, lift.y, lift.z) * (zoom * chaseR * PilotViewLift);
                }
                // At a gun mount the eye is the mount's own, not a chase (U.SPC-T445).
                if (PilotEye != null) ScenePosition = PilotEye();
                CurrentSpeed = 0;
                return;
            }
            if (input.LookHeld)
            {
                orbitYaw += input.LookDelta.x * LookSensitivity;
                orbitPitch = Mathf.Clamp(orbitPitch - LookY(input.LookDelta.y) * LookSensitivity, -88f, 88f);
            }
            if (Math.Abs(input.Scroll) > 0)
                zoomTarget = Math.Max(MinZoom(FocusTarget), Math.Min(5e7, zoomTarget * Math.Pow(0.82, input.Scroll)));
            zoom = Math.Exp(Math.Log(zoom) + (Math.Log(zoomTarget) - Math.Log(zoom)) * (1 - Math.Exp(-dt * 10)));

            Rotation = UpFrame * Quaternion.Euler(orbitPitch, orbitYaw, 0);
            var back = Rotation * Vector3.back;
            double r = world.TargetRadius(FocusTarget);
            ScenePosition = world.TargetScene(FocusTarget) + new DVec3(back.x, back.y, back.z) * (zoom * r);
            CurrentSpeed = 0;
        }

        void UpdateTravel(float dt)
        {
            travelT = Mathf.Min(1f, travelT + dt / TravelSeconds);
            float e = SmootherStep(travelT);

            var target = world.TargetScene(FocusTarget);
            double r = world.TargetRadius(FocusTarget);
            double startDist = Math.Max(travelStartOffset.Length, 1e-9);
            double endDist = travelEndZoom * r;
            double dist = Math.Exp(Math.Log(startDist) + (Math.Log(endDist) - Math.Log(startDist)) * e);

            var startDir = (travelStartOffset / startDist);
            Vector3 dir;
            Quaternion lookAt;
            if (PilotLocked)
            {
                // Settles in behind the hull's own facing from the start (U.SPC-T370), not round whatever side
                // the camera already happened to be on: a chase has no "approach from the side we're on", only
                // the hull's own forward, which the player may already be turning under their own input while
                // this plays out. Read live (not just at the moment the chase began) so a turn mid-transition
                // is followed rather than fought.
                var shipRotation = world.TargetRotation(FocusTarget);
                var endDirV = shipRotation * Vector3.back;
                dir = Vector3.Slerp(ToVector(startDir), endDirV, e).normalized;
                lookAt = PilotEye != null && PilotView != null ? PilotView.Value : shipRotation;
            }
            else
            {
                dir = Vector3.Slerp(ToVector(startDir), ToVector(travelEndDirection), e).normalized;
                lookAt = Quaternion.LookRotation(-dir, Up);
            }
            ScenePosition = target + new DVec3(dir.x, dir.y, dir.z) * dist;
            // Gliding to a gun mount (U.SPC-T445): the glide ends at the mount's eye, not behind the hull.
            if (PilotLocked && PilotEye != null)
            {
                var eye = PilotEye();
                ScenePosition = ScenePosition + (eye - ScenePosition) * e;
            }
            Rotation = Quaternion.Slerp(travelStartRotation, lookAt, SmootherStep(Mathf.Min(1f, travelT * 1.8f)));

            if (travelT >= 1f)
            {
                Mode = CameraMode.Focus;
                zoom = zoomTarget = travelEndZoom;
            }
        }

        static float SmootherStep(float t) => t * t * t * (t * (t * 6 - 15) + 10);
        static Vector3 ToVector(DVec3 v) => new Vector3((float)v.X, (float)v.Y, (float)v.Z);

        // ---------------------------------------------------------------- reference frames

        DVec3 FramePosition()
        {
            var body = world.Catalog[frame];
            return world.BodyScene(frame) + world.Scale.LocalOffset(body, trueOffset);
        }

        /// <summary>Moves by a scene-space delta, converted to true km through the local scale mapping's Jacobian.</summary>
        void ApplySceneDelta(DVec3 delta)
        {
            var body = world.Catalog[frame];
            var scale = world.Scale;
            double d = trueOffset.Length;
            if (d <= 0)
            {
                trueOffset = delta.SwapYZ * (body.RadiusKm / Math.Max(scale.DisplayRadius(body), 1e-12));
                return;
            }
            double sceneDist = scale.LocalDistance(body, d);
            var dirScene = trueOffset.SwapYZ / d;
            double radial = DVec3.Dot(delta, dirScene);
            var tangential = delta - dirScene * radial;
            double derivative = Math.Max(scale.LocalDistanceDerivative(body, d), 1e-15);
            var trueDeltaScene = dirScene * (radial / derivative) + tangential * (d / sceneDist);
            trueOffset += trueDeltaScene.SwapYZ;

            double minimum = body.RadiusKm * 1.002;
            if (trueOffset.Length < minimum) trueOffset = trueOffset.Normalized * minimum;
        }

        /// <summary>Re-anchors to whichever body's sphere of influence contains the scene point, keeping the scene position.</summary>
        public void SetScenePosition(DVec3 scenePosition)
        {
            int f = FrameAt(scenePosition);
            frame = f;
            trueOffset = world.Scale.InverseLocalOffset(world.Catalog[f], scenePosition - world.BodyScene(f));
            ScenePosition = scenePosition;
        }

        /// <summary>The innermost body whose sphere of influence contains the scene point (the root if none does).
        /// <see cref="FrameBody"/> is only kept up to date while flying free; this holds in any mode.</summary>
        public int FrameAt(DVec3 scenePosition)
        {
            int f = 0;
            while (true)
            {
                int best = -1;
                double bestRatio = 1.0;
                foreach (int child in world.Catalog.ChildrenOf(f))
                {
                    double soi = world.Scale.SceneSoi(world.Catalog[child]);
                    double ratio = (scenePosition - world.BodyScene(child)).Length / soi;
                    if (ratio < bestRatio)
                    {
                        bestRatio = ratio;
                        best = child;
                    }
                }
                if (best < 0) break;
                f = best;
            }
            return f;
        }

        /// <summary>Frame hysteresis: enter a child's SOI below 92% of it, leave ours above 108%.</summary>
        void UpdateFrame()
        {
            var pos = FramePosition();
            if (frame != 0)
            {
                var body = world.Catalog[frame];
                if ((pos - world.BodyScene(frame)).Length > world.Scale.SceneSoi(body) * 1.08)
                {
                    int parent = body.ParentIndex;
                    frame = parent;
                    trueOffset = world.Scale.InverseLocalOffset(world.Catalog[parent], pos - world.BodyScene(parent));
                    return;
                }
            }
            foreach (int child in world.Catalog.ChildrenOf(frame))
            {
                var c = world.Catalog[child];
                if ((pos - world.BodyScene(child)).Length < world.Scale.SceneSoi(c) * 0.92)
                {
                    frame = child;
                    trueOffset = world.Scale.InverseLocalOffset(c, pos - world.BodyScene(child));
                    return;
                }
            }
        }

        bool PushOutOfBodies()
        {
            var pos = ScenePosition;
            bool pushed = false;
            for (int i = 0; i < world.Catalog.Count; i++)
            {
                var c = world.BodyScene(i);
                double minimum = world.Scale.DisplayRadius(world.Catalog[i]) * 1.003;
                var d = pos - c;
                double len = d.Length;
                if (len < minimum)
                {
                    pos = c + (len > 0 ? d / len : DVec3.UnitY) * minimum;
                    pushed = true;
                }
            }
            if (pushed) SetScenePosition(pos);
            return pushed;
        }
    }
}
