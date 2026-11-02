using Unity.SpaceGod.Core;

namespace Unity.SpaceGod
{
    /// <summary>
    /// The hauler at the helm (U.SPC-T443, <see cref="HaulerFlightModel"/>, docs/FLIGHT_MODELS.md, Haulers): what the pilot loop
    /// tells the hauler model each frame (how laden the hull is, and whether it is inside a port's approach, where Ctrl is
    /// docking mode), and what the HUD shows of it (the full-retro stop point, and docking mode).
    /// </summary>
    public sealed partial class UnitySpaceApp
    {
        /// <summary>Where a piloted hauler comes to rest under full retro, with what it carries aboard; null for any other hull.</summary>
        public HaulerStop? PilotStop { get; private set; }

        /// <summary>Whether the piloted hauler flew this frame in docking mode (Ctrl held inside a port's approach).</summary>
        public bool PilotDocking { get; private set; }

        /// <summary>Whether the piloted hauler is inside a port's approach (U.SPC-T443): near enough a port of the world it flies
        /// by to deal with it (<see cref="NearPort"/>), welcome or not.</summary>
        public bool PilotInPortApproach { get; private set; }

        /// <summary>Whether the hull is inside a port's approach this frame, for the helm to fly a hauler's docking mode in
        /// (the load aboard and Ctrl are the helm's own, U.SPC-T449).</summary>
        bool PortApproach()
        {
            PilotInPortApproach = false;
            foreach (var s in Economy.At(pilotFrameBody))
                if (NearPort(s))
                {
                    PilotInPortApproach = true;
                    break;
                }
            return PilotInPortApproach;
        }

        /// <summary>After the hauler model has flown the frame: the stop point and docking mode, for the HUD.</summary>
        void HaulerReadout(IFlightModel model, in HullDynamics hull, in PilotInput input)
        {
            var hauler = model as HaulerFlightModel;
            PilotStop = hauler != null ? hauler.StopPoint(pilot, hull, input) : (HaulerStop?)null;
            PilotDocking = hauler != null && HaulerFlightModel.Docking(input);
        }
    }
}
