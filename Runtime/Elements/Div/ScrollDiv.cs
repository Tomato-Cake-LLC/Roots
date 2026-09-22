using RishUI;
using UnityEngine;
using UnityEngine.UIElements;

namespace Roots
{
    // A Div that scrolls. UIToolkit's own ScrollView handles the wheel, drag and scrollbar: its
    // internals are not ICustomPicking, so Roots' pointer-detection inheritance leaves them alone and
    // they stay interactive. Children arrive through Add(), which ScrollView routes into its content.
    //
    // Needs --pointer-detection: rect to receive the wheel, like any other Roots element.
    public partial class ScrollDiv : UnityEngine.UIElements.ScrollView, IVisualElement
    {
        private Bridge Bridge { get; }
        Bridge IVisualElement.Bridge => Bridge;

        private PickingManager PickingManager { get; }
        PickingManager ICustomPicking.Manager => PickingManager;

        public ScrollDiv()
        {
            Bridge = new Bridge(this);
            PickingManager = new RectPickingManager(Bridge);
        }

        void IVisualElement.Setup() { }

        public override bool ContainsPoint(Vector2 localPoint) => PickingManager.ContainsPoint(localPoint);
    }
}
