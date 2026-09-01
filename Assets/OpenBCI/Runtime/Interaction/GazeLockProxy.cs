using UnityEngine;

namespace OpenBCI.Interaction
{
    /// <summary>
    /// Sits on the invisible gaze-lock collider that <see cref="EEGMover"/> spawns over an
    /// object's travel path. Lets <see cref="GazeEEGInteractor"/> resolve a gaze-ray hit on
    /// that collider back to the object it belongs to, so the selection stays put while the
    /// object moves out from under a still head.
    /// </summary>
    public class GazeLockProxy : MonoBehaviour
    {
        public EEGSelectable target;
    }
}
