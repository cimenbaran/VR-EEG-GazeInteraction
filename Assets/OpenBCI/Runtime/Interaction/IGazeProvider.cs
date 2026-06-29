using UnityEngine;

namespace OpenBCI.Interaction
{
    /// <summary>
    /// Supplies the ray the user is "looking" along. Implemented today by
    /// <see cref="HeadGazeProvider"/> (headset forward ray); an eye-tracking
    /// provider can be dropped in later without touching the interactor.
    /// </summary>
    public interface IGazeProvider
    {
        /// <summary>True if a valid gaze ray is available this frame.</summary>
        bool TryGetGazeRay(out Ray ray);
    }
}
