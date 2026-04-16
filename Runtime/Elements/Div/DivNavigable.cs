using RishUI;
using RishUI.MemoryManagement;

namespace Roots
{
    // Navigable overloads for Div.Create() — generated overloads don't include navigable.
    // Use these when the element should auto-register with the nearest NavigationGroup.
    public partial class Div
    {
        [RequiresManagedContext]
        public static Element Create(ClassName className, Navigable navigable, Children? children = null) =>
            Rish.Create<Div>(new VisualAttributes { className = className }, navigable, children);

        [RequiresManagedContext]
        public static Element Create(
            VisualAttributes attributes,
            Navigable navigable,
            Children? children = null) =>
            Rish.Create<Div>(attributes, navigable, children);

        [RequiresManagedContext]
        public static Element Create(
            ulong key,
            VisualAttributes attributes,
            Navigable navigable,
            Children? children = null) =>
            Rish.Create<Div>(key, attributes, navigable, children);
    }
}
