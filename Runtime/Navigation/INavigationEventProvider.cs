#nullable enable

using System;
using UnityEngine;

// Abstracts where navigate/submit/cancel events come from.
// Decouples NavigationGroup from Unity's InputSystem, enabling testing and future input backends.
public interface INavigationEventProvider : IDisposable {
    event Action<Vector2> Navigate;
    event Action NavigateCanceled;
    // started (not performed) so hold buttons receive A-button-down immediately.
    event Action Submit;
    event Action SubmitEnded;
    event Action Cancel;
}
